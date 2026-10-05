#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
wows_ship_scraper.py —— 360 战舰助手（国服）舰船数据全量爬虫。

【和旧版的区别】
旧版是一个必须装到手机上的 Android App（WowsScraper），靠悬浮窗触发、要在 Android
Studio 里编译、还要申请悬浮窗权限——任何一环出问题就整体"失效"，而且维护成本极高。
实测表明 **接口本身一直是通的**（HTTP 200 / errno=0 / total=963），坏的是那个载体。
所以本版改成纯 Python，直接在电脑上跑，不再依赖手机与 Android 工具链。

【"以后软件更新也不用改代码"是怎么做到的】
1. 常量不硬编码，而是**从 APK 里自动提取候选 + 在线验证**：
   给 --apk 时，扫描 dex 的字符串池，抓出 base url / api 路径 / 32 位 hex 盐 /
   8 字节 IV 候选，然后真的发请求去试，哪个组合能解出合法 JSON 就用哪个。
   360 换了盐、换了域名、换了接口路径，只要把新 APK 丢进来就能自己适配。
2. 解密自适应：依次尝试 明文 / DES-CBC / DES-ECB / AES-CBC / AES-ECB，
   密钥派生方式（MD5(sign) 的各个切片、sign 本身的切片）也全部枚举，
   判据是"能不能解出带 errno 字段的合法 JSON"，而不是靠猜。
3. 字段自适应：ship_id / id / tank_id、name / ship_name / title 都做兜底查找。
4. 分页自适应：优先信服务端返回的 total/next，同时也支持"拉到空页为止"。
5. 完全不给 APK 也能跑：内置上一版验证过的常量作为兜底，提取失败自动降级。

【依赖】
零第三方依赖。DES 用同目录下的 des_pure.py（纯标准库手写实现）。

【典型用法】
  # 全自动：从 APK 提取常量 + 爬全量 + 更新助手知识库
  python wows_ship_scraper.py --apk prod490004-130002-1.3.0002.apk --update-index

  # 不爬 AI 评价（快很多，约 1/3 时间）
  python wows_ship_scraper.py --apk xxx.apk --no-review --update-index

  # 只探测一下现在接口通不通、拿到多少艘
  python wows_ship_scraper.py --apk xxx.apk --probe-only
"""
import argparse
import base64
import concurrent.futures as futures
import hashlib
import json
import os
import re
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import des_pure  # noqa: E402

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

UA = 'okhttp/4.12.0'

# ---------------------------------------------------------------- 兜底常量
# 来源：上一版 WowsScraper（已反编译验证）。APK 提取失败时用它，提取成功则以 APK 为准。
FALLBACK = {
    'base': 'https://wbox.wows.360.cn',
    'salt': '701c6ab332a91741b610103a5e507fe7',
    'iv': '31472856',
    'v': '130002',
    'path_list': '/wiki/shipsv2',
    'path_detail': '/wiki/shipprofilev2',
    'path_comment': '/cms/commentList',
}

# 已知的算法"指纹"：key 派生方式 × 算法 × 模式。全部枚举，靠在线结果判定哪个对。
KEY_DERIVATIONS = [
    ('md5(sign)[6:14]', lambda s: md5s(s)[6:14]),
    ('md5(sign)[0:8]', lambda s: md5s(s)[0:8]),
    ('md5(sign)[8:16]', lambda s: md5s(s)[8:16]),
    ('md5(sign)[16:24]', lambda s: md5s(s)[16:24]),
    ('md5(sign)[24:32]', lambda s: md5s(s)[24:32]),
    ('sign[0:8]', lambda s: s[0:8]),
    ('sign[8:16]', lambda s: s[8:16]),
    ('sign[16:24]', lambda s: s[16:24]),
    ('sign[24:32]', lambda s: s[24:32]),
]


def md5s(s):
    return hashlib.md5(s.encode('utf-8')).hexdigest()


# ---------------------------------------------------------------- APK 常量提取

def _dex_strings(apk_path, min_len=4, limit=400000):
    """从 APK 的 classes*.dex 里粗提 ASCII 字符串。

    不解析完整 DEX 结构（没必要也很脆）：DEX 的字符串池里存的是 MUTF-8，
    ASCII 部分与原始字节一致，直接按"连续可打印字符"扫即可，足够定位常量。
    """
    out = []
    pat = re.compile(rb'[\x20-\x7e]{%d,}' % min_len)
    try:
        with zipfile.ZipFile(apk_path) as z:
            names = [n for n in z.namelist()
                     if re.match(r'classes\d*\.dex$', os.path.basename(n))]
            if not names:
                names = [n for n in z.namelist() if n.endswith('.dex')]
            for n in names:
                data = z.read(n)
                for m in pat.finditer(data):
                    out.append(m.group().decode('ascii', 'replace'))
                    if len(out) >= limit:
                        return out
    except Exception as e:
        print('  [警告] 读取 APK 失败：%s' % e, file=sys.stderr)
    return out


# 各类候选的最大数量。提取是启发式的，宁可少而精——反正有在线验证兜底，
# 但候选太多会把探测拖成几万次请求，那就本末倒置了。
CAP = {'base': 8, 'salt': 40, 'iv': 24, 'v': 8, 'paths': 40}


def extract_from_apk(apk_path):
    """从 APK 里提取候选常量。返回 dict of list（都是候选，待在线验证）。

    【踩过的坑】DEX 的字符串池里字符串是紧挨着存的，前一个串的 MUTF-8 长度字节
    常常恰好落在 0x20-0x7e，会被一起扫进来，于是真实常量往往被包在一个更长的
    垃圾串中间（例如 'Phttps://wbox.wows.360.cn/activity.html#/wiki'）。
    所以这里**不能**对整串做 fullmatch，必须用 re.search 从串中间抠出来。
    """
    print('[*] 扫描 APK 提取常量候选：%s' % os.path.basename(apk_path))
    strs = _dex_strings(apk_path)
    print('    提取到 %d 条字符串' % len(strs))
    if not strs:
        return {}

    cand = {'base': [], 'salt': [], 'iv': [], 'v': [], 'paths': []}
    blob = '\n'.join(strs)   # 拼起来一起搜，避免漏掉跨串边界的情况

    def add(key, val):
        if val not in cand[key]:
            cand[key].append(val)

    # --- base url：从串中间抠出 http(s)://host，只要与 WoWS/360 相关的
    scored = {}
    for s in strs:
        for m in re.finditer(r'https?://([A-Za-z0-9.\-]+)', s):
            host = m.group(1).lower()
            if any(k in host for k in ('wows', 'wbox', 'wowsbox', 'qihoo')):
                scored.setdefault('https://' + host, 0)
                # wbox 是 API 网关，权重最高；wows 次之
                scored['https://' + host] += (3 if 'wbox' in host else 1)
    for host, _ in sorted(scored.items(), key=lambda kv: -kv[1]):
        if len(cand['base']) >= CAP['base']:
            break
        add('base', host)

    # --- 接口路径
    for m in re.finditer(r'/(wiki|cms|api)/[A-Za-z0-9_]{2,30}', blob):
        if len(cand['paths']) >= CAP['paths']:
            break
        add('paths', m.group())

    # --- 签名盐：32 位十六进制。一定有噪声，靠在线验证筛。
    salts = {}
    for m in re.finditer(r'(?<![0-9a-zA-Z])[0-9a-f]{32}(?![0-9a-zA-Z])', blob):
        salts[m.group()] = salts.get(m.group(), 0) + 1
    for s, _ in sorted(salts.items(), key=lambda kv: -kv[1])[:CAP['salt']]:
        add('salt', s)

    # --- IV：DES 是 8 字节、AES 是 16 字节。只收"纯数字 8 位"这类最像 IV 的，
    #     其它一律不要——实测全收会拿到两千多个随机串，纯属拖慢枚举。
    for m in re.finditer(r'(?<![0-9])[0-9]{8}(?![0-9])', blob):
        if len(cand['iv']) >= CAP['iv']:
            break
        add('iv', m.group())
    for s in (FALLBACK['iv'], '12345678', '00000000'):
        cand['iv'].insert(0, s) if s not in cand['iv'] else None
    cand['iv'] = list(dict.fromkeys(cand['iv']))[:CAP['iv']]

    # --- 版本号 v：6 位数字、以 1 开头
    vs = {}
    for m in re.finditer(r'(?<![0-9])1[0-9]{5}(?![0-9])', blob):
        vs[m.group()] = vs.get(m.group(), 0) + 1
    for s, _ in sorted(vs.items(), key=lambda kv: -kv[1])[:CAP['v']]:
        add('v', s)

    for k in ('base', 'salt', 'iv', 'v', 'paths'):
        print('    %-6s %d 个候选 %s' % (k, len(cand[k]), cand[k][:5]))
    return cand


# ---------------------------------------------------------------- 请求与签名

class Api:
    """封装好 base/salt/iv/版本 之后的一次性请求工具。"""

    def __init__(self, base, salt, iv, v):
        self.base = base.rstrip('/')
        self.salt = salt
        self.iv = iv.encode('utf-8')
        self.v = v
        self._lock = threading.Lock()
        self.nreq = 0

    def build_url(self, path, extra):
        p = {
            'platform': '1', 'v': self.v, 'ch': 'ch_wg_default',
            'sk': '33', 'md': 'PC', 'brand': 'PC',
            'm1': '', 'm2': '', 'm3': '', 'nt': 'wifi', 'oaid': '',
            '_t': str(int(time.time() * 1000)),
        }
        p.update(extra or {})
        skip = {'m1', 'm2', 'm3', 'oaid'}
        items = sorted((k, v) for k, v in p.items()
                       if k != 'sign' and not (v == '' and k in skip))
        raw = '&'.join('%s=%s' % kv for kv in items) + self.salt
        sign = md5s(raw)
        q = urllib.parse.urlencode(items + [('sign', sign)])
        return '%s%s?%s' % (self.base, path, q), sign

    def get(self, url, timeout=25, retries=3):
        last = None
        for i in range(retries):
            try:
                req = urllib.request.Request(url, headers={'User-Agent': UA})
                with urllib.request.urlopen(req, timeout=timeout) as r:
                    with self._lock:
                        self.nreq += 1
                    return r.read().decode('utf-8', 'replace')
            except Exception as e:
                last = e
                time.sleep(0.4 * (i + 1))
        raise IOError('GET 失败(%d 次): %s' % (retries, last))


# ---------------------------------------------------------------- 自适应解密

def _aes_available():
    try:
        import Crypto.Cipher.AES  # noqa: F401
        return True
    except Exception:
        return False


def try_decrypt(body, sign, iv_candidates):
    """依次尝试各种"算法 × 密钥派生 × IV"，返回第一个能解出合法 JSON 的结果。

    判据很硬：必须是能被 json.loads 解析、且含 errno 键的对象。
    这样即使 360 把 DES 换成 AES、或者改了密钥截取位置，也能自己找到对的组合。
    """
    if body is None:
        return None, 'empty'

    # 1) 先看是不是根本没加密
    s = body.strip()
    if s.startswith('{') or s.startswith('['):
        try:
            obj = json.loads(s)
            if isinstance(obj, dict) and 'errno' in obj:
                return obj, 'plain'
        except Exception:
            pass

    raw = None
    try:
        raw = base64.b64decode(body)
    except Exception:
        return None, 'not-base64'
    if len(raw) < 8:
        return None, 'too-short'

    best_partial = None
    for dname, dfunc in KEY_DERIVATIONS:
        try:
            kstr = dfunc(sign)
        except Exception:
            continue
        key = kstr.encode('utf-8')

        # DES（8 字节密钥）
        if len(key) >= 8:
            for iv in iv_candidates:
                ivb = iv.encode('utf-8')[:8]
                if len(ivb) < 8:
                    ivb = ivb + b'\x00' * (8 - len(ivb))
                for mode in ('cbc', 'ecb'):
                    try:
                        if mode == 'cbc':
                            pt = des_pure.des_cbc_decrypt(key[:8], ivb, raw)
                        else:
                            # ECB = 逐块独立解密；用全零 IV 的 CBC 分块实现
                            pt = b''.join(
                                des_pure.des_cbc_decrypt(key[:8], b'\x00' * 8, raw[i:i + 8])
                                for i in range(0, len(raw), 8))
                        txt = des_pure.strip_pkcs5(pt).decode('utf-8', 'replace')
                        obj = json.loads(txt)
                        if isinstance(obj, dict) and 'errno' in obj:
                            return obj, 'DES-%s/%s/IV=%s' % (mode.upper(), dname, iv)
                        if best_partial is None and ('errno' in txt or 'data' in txt):
                            best_partial = 'DES-%s/%s 解出文本但不像 JSON' % (mode, dname)
                    except Exception:
                        continue

        # AES（16 字节密钥），仅在装了 pycryptodome 时才有意义
        if _aes_available() and len(key) >= 16:
            try:
                from Crypto.Cipher import AES
                for iv in iv_candidates:
                    ivb = iv.encode('utf-8')[:16]
                    if len(ivb) < 16:
                        ivb = ivb + b'\x00' * (16 - len(ivb))
                    for mode in (AES.MODE_CBC, AES.MODE_ECB):
                        try:
                            c = AES.new(key[:16], mode, ivb) if mode == AES.MODE_CBC \
                                else AES.new(key[:16], mode)
                            pt = c.decrypt(raw[:len(raw) // 16 * 16])
                            txt = des_pure.strip_pkcs5(pt).decode('utf-8', 'replace')
                            obj = json.loads(txt)
                            if isinstance(obj, dict) and 'errno' in obj:
                                return obj, 'AES/%s/IV=%s' % (dname, iv)
                        except Exception:
                            continue
            except Exception:
                pass

    return None, best_partial or ('全部 %d 种组合都解不出合法 JSON' % len(KEY_DERIVATIONS))


# ---------------------------------------------------------------- 在线验证常量

def probe_config(api_candidates, path_list, budget=240):
    """找出真正可用的 (base, salt, iv, v) 组合。

    【顺序很关键】
      第 1 轮：只试内置兜底常量。绝大多数情况下 1 次请求就命中，秒开。
      第 2 轮：兜底失效（说明 360 改了东西）时才穷举 APK 里挖出来的候选。
    这样"平常快、出事能自愈"，不至于每次都要把几千个候选跑一遍。

    每个 (base, salt, v) 组合只发 1 次请求；IV / 密钥派生 / 算法的组合全部
    **本地**枚举（不发请求），所以网络开销是可控的。
    """
    def dedup(lst, known):
        out = [known] if known else []
        for x in (lst or []):
            if x not in out:
                out.append(x)
        return out

    c = api_candidates or {}
    bases = dedup(c.get('base'), FALLBACK['base'])
    salts = dedup(c.get('salt'), FALLBACK['salt'])
    vs = dedup(c.get('v'), FALLBACK['v'])
    ivs = dedup(c.get('iv'), FALLBACK['iv'])

    # 版本号按"离已知版本最近"排，越像当前版本越先试
    def vkey(x):
        try:
            return abs(int(x) - int(FALLBACK['v']))
        except Exception:
            return 10 ** 9
    vs = sorted(set(vs), key=vkey)

    rounds = [
        ([FALLBACK['base']], [FALLBACK['salt']], [FALLBACK['v']], '内置常量'),
        (bases, salts, vs, 'APK 候选穷举'),
    ]

    tried = 0
    sign_rejected = False
    net_error = False
    for rb, rs, rv, label in rounds:
        combos = [(b, v, s) for b in rb for v in rv for s in rs]
        print('[*] 在线验证（%s）：%d 个组合，IV/算法本地枚举 %d 种'
              % (label, len(combos), len(ivs)))
        for base, v, salt in combos:
            if tried >= budget:
                print('    ! 达到尝试上限 %d，停止' % budget)
                break
            tried += 1
            api = Api(base, salt, FALLBACK['iv'], v)
            url, sign = api.build_url(path_list, {'page_no': '1', 'length': '5'})
            try:
                body = api.get(url, retries=1, timeout=15)
            except Exception as e:
                print('    x %s v=%s salt=%s… 请求失败 %s' % (base, v, salt[:8], e))
                net_error = True
                continue
            obj, how = try_decrypt(body, sign, ivs)
            if obj is None:
                print('    x %s v=%s salt=%s… %s' % (base, v, salt[:8], how))
                continue
            if obj.get('errno') != 0:
                print('    x %s v=%s salt=%s… errno=%s(%s)'
                      % (base, v, salt[:8], obj.get('errno'), obj.get('errmsg')))
                # 实测：盐不对时服务端返回 errno=400 "sign check wrong"（响应本身仍可解密，
                # 因为 DES 密钥由我们自己算的 sign 派生）。抓住这个就能精确区分
                # "签名过期" 和 "接口挂了/网络问题"，而不是笼统报个失败。
                if str(obj.get('errmsg', '')).lower().find('sign') >= 0:
                    sign_rejected = True
                continue

            data = obj.get('data') or {}
            total = data.get('total') or 0
            if not total and isinstance(data, dict):
                total = len(data.get('data') or [])
            m = re.search(r'IV=([^/\)]+)', how)
            iv_hit = m.group(1) if m else FALLBACK['iv']
            print('    √ 命中  base=%s  v=%s  salt=%s…  IV=%s  解密=%s  total=%s'
                  % (base, v, salt[:8], iv_hit, how, total))
            if salt != FALLBACK['salt'] or base != FALLBACK['base']:
                print('    ! 注意：本次用的是 APK 里新提取的常量，'
                      '建议把它们回填到脚本的 FALLBACK 里')
            return Api(base, salt, iv_hit, v), int(total or 0), how
        else:
            continue
        break

    if sign_rejected and not net_error:
        why = ('签名被服务端拒绝 (errno=400 sign check wrong) —— 签名盐已过期，'
               '这是唯一需要 APK 的场景')
    elif net_error and not sign_rejected:
        why = '请求发不出去 —— 检查网络 / 代理 / 360 接口是否临时维护'
    else:
        why = '所有候选组合都失败（共试 %d 次）' % tried
    return None, 0, why


# ---------------------------------------------------------------- 抓取

def ship_id_of(obj):
    for k in ('ship_id', 'shipId', 'id', 'tank_id'):
        v = obj.get(k)
        if v is not None and str(v).strip():
            return str(v)
    return None


def ship_name_of(obj):
    for k in ('name', 'ship_name', 'title', 'shipName'):
        v = obj.get(k)
        if v:
            return str(v)
    return ''


def fetch_list(api, path_list, page_size=20, workers=6):
    print('[1/3] 拉取舰船列表…')
    url, sign = api.build_url(path_list, {'page_no': '1', 'length': str(page_size)})
    first, how = try_decrypt(api.get(url), sign, [api.iv.decode('utf-8', 'replace')])
    if first is None:
        raise RuntimeError('首页列表解密失败：%s' % how)
    data = first.get('data') or {}
    rows = list(data.get('data') or [])
    total = int(data.get('total') or 0)
    if total <= 0:
        total = len(rows)
    total_pages = max(1, (total + page_size - 1) // page_size)
    print('      共 %d 艘 / %d 页，并发拉取…' % (total, total_pages))

    if total_pages > 1:
        def one(p):
            u, sg = api.build_url(path_list, {'page_no': str(p), 'length': str(page_size)})
            for attempt in range(3):
                try:
                    o, _ = try_decrypt(api.get(u), sg, [api.iv.decode('utf-8', 'replace')])
                    if o is None:
                        raise IOError('解密失败')
                    d = o.get('data') or {}
                    return list(d.get('data') or [])
                except Exception:
                    if attempt == 2:
                        return []
                    time.sleep(0.5 * (attempt + 1))
            return []

        with futures.ThreadPoolExecutor(max_workers=workers) as ex:
            for i, r in enumerate(ex.map(one, range(2, total_pages + 1)), start=2):
                rows.extend(r)
                if i % 10 == 0 or i == total_pages:
                    print('      列表 %d/%d 页（累计 %d 艘）' % (i, total_pages, len(rows)))

    # 去重（服务端分页偶尔会抖，重复也无害，但去重后 id 才准确）
    uniq = {}
    for r in rows:
        sid = ship_id_of(r)
        if sid:
            uniq[sid] = r
    print('      列表完成：%d 艘（去重后）' % len(uniq))
    return list(uniq.values()), total


def fetch_detail(api, path_detail, sid, iv):
    u, sg = api.build_url(path_detail, {'shipId': sid})
    for attempt in range(3):
        try:
            o, _ = try_decrypt(api.get(u), sg, [iv])
            if o is None or o.get('errno') != 0:
                return {}
            d = o.get('data')
            return d if isinstance(d, dict) else {}
        except Exception:
            if attempt == 2:
                return {}
            time.sleep(0.4 * (attempt + 1))
    return {}


def fetch_review(api, path_comment, sid, iv):
    """AI 评价（360 服务端预生成，不是本地大模型）。拿不到就返回空串，不影响主流程。"""
    u, sg = api.build_url(path_comment,
                          {'shipId': sid, 'sort': '1', 'pageNo': '1', 'size': '20'})
    try:
        o, _ = try_decrypt(api.get(u, retries=2), sg, [iv])
        if o is None or o.get('errno') != 0:
            return ''
        d = o.get('data') or {}
        ai = d.get('ai_comment')
        if isinstance(ai, dict):
            arr = ai.get('data') or []
            if arr and isinstance(arr[0], dict):
                return str(arr[0].get('content') or '')
    except Exception:
        pass
    return ''


def scrape(api, paths, want_review=True, detail_workers=8, page_size=20, limit=0):
    summaries, total = fetch_list(api, paths['list'], page_size=page_size)
    if not summaries:
        raise RuntimeError('列表为空，无法继续')
    if limit and len(summaries) > limit:
        summaries = summaries[:limit]
        print('      （--limit %d，只爬前 %d 艘）' % (limit, limit))

    iv = api.iv.decode('utf-8', 'replace')
    out = []
    lock = threading.Lock()
    done = 0
    t0 = time.time()

    def work(summary):
        nonlocal done
        sid = ship_id_of(summary)
        if not sid:
            return
        merged = dict(summary)
        detail = fetch_detail(api, paths['detail'], sid, iv)
        for k, v in (detail or {}).items():
            if k not in merged or merged[k] in (None, '', []):
                merged[k] = v
        merged['ai_review'] = fetch_review(api, paths['comment'], sid, iv) \
            if want_review else ''
        with lock:
            out.append(merged)
            done += 1
            if done % 50 == 0 or done == len(summaries):
                el = time.time() - t0
                sp = done / el if el > 0 else 0
                print('      %d/%d (%.0f%%)  %.1f 艘/s  剩余 ~%ds'
                      % (done, len(summaries), done * 100.0 / len(summaries),
                         sp, (len(summaries) - done) / sp if sp > 0 else 0))

    print('[2/3] 拉取详情%s…' % ('与 AI 评价' if want_review else ''))
    with futures.ThreadPoolExecutor(max_workers=detail_workers) as ex:
        list(ex.map(work, summaries))

    byid = {}
    for s in out:
        sid = ship_id_of(s)
        if sid:
            byid[sid] = s
    result = list(byid.values())
    result.sort(key=lambda s: (int(s.get('tier') or 0), ship_name_of(s)))
    print('      详情完成：%d 艘（用时 %.0fs）' % (len(result), time.time() - t0))
    return result, total


# ---------------------------------------------------------------- 输出

def write_db(ships, outdir, tag=None):
    os.makedirs(outdir, exist_ok=True)
    ts = tag or datetime.now().strftime('%Y%m%d_%H%M%S')
    path = os.path.join(outdir, 'wows_ships_data_%s.json' % ts)
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(ships, f, ensure_ascii=False, separators=(',', ':'))
    return path


def build_index(ships):
    """ship_id -> "中文名|等级|舰种|国家"（与助手项目现有格式完全一致）"""
    idx = {}
    for s in ships:
        sid = ship_id_of(s)
        nm = ship_name_of(s)
        if not sid or not nm:
            continue
        idx[str(sid)] = '%s|%s|%s|%s' % (nm, s.get('tier', ''), s.get('vtype', ''),
                                         s.get('nation', ''))
    return idx


def diff_index(old_path, new_idx):
    """和旧索引比一比，告诉用户新增/改名了哪些船。"""
    if not old_path or not os.path.exists(old_path):
        return None
    try:
        with open(old_path, encoding='utf-8') as f:
            old = json.load(f)
    except Exception:
        return None
    old_ids = set(old)
    new_ids = set(new_idx)
    added = sorted(new_ids - old_ids, key=lambda k: new_idx[k])
    removed = sorted(old_ids - new_ids)
    renamed = []
    for k in sorted(old_ids & new_ids):
        on = old[k].split('|')[0]
        nn = new_idx[k].split('|')[0]
        if on != nn:
            renamed.append((k, on, nn))
    return {'old_count': len(old_ids), 'added': added, 'removed': removed,
            'renamed': renamed, 'old': old, 'new': new_idx}


# ---------------------------------------------------------------- 主流程

def do_check(api, total, index_path):
    """体检：1 次请求看清楚现在什么状况，不用等 6 分钟全量。"""
    print()
    print('=' * 62)
    print('  体检结果')
    print('=' * 62)
    print('  接口      ：正常（HTTP 200 / errno=0 / DES 解密通过）')
    print('  服务端舰船：%d 艘' % total)

    local = 0
    if index_path and os.path.exists(index_path):
        try:
            with open(index_path, encoding='utf-8') as f:
                local = len(json.load(f))
        except Exception:
            local = 0
    print('  本地索引  ：%d 条%s' % (local, '' if local else '（还没生成过）'))
    print()

    need = False
    if local == 0:
        print('  → 本地还没有索引，需要完整抓取。')
        need = True
    elif local < total:
        print('  → 落后 %d 艘，建议更新。' % (total - local))
        need = True
    elif local > total:
        print('  → 本地比服务端还多 %d 条（服务端可能下架了部分船），'
              '更新后会以服务端为准。' % (local - total))
        need = True
    else:
        print('  → 数量已一致，无需更新（除非想刷新描述/AI 评价等细节）。')
    print()
    if need:
        print('  更新命令：python wows_ship_scraper.py --update-index（约 6 分钟）')
    print('=' * 62)
    # 退出码给批处理用：10 = 建议更新，0 = 已是最新，省掉无谓的 6 分钟等待
    return 10 if need else 0


def resolve_paths(cand):
    """确定三个接口路径：APK 里挖到的优先，否则用兜底。"""
    paths = {}
    got = cand.get('paths') or []
    for key, fb, hint in (('list', FALLBACK['path_list'], 'ships'),
                          ('detail', FALLBACK['path_detail'], 'shipprofile'),
                          ('comment', FALLBACK['path_comment'], 'commentList')):
        hit = next((p for p in got if hint in p.lower()), None)
        paths[key] = hit or fb
    return paths


def main(argv):
    ap = argparse.ArgumentParser(description='360 战舰助手舰船数据全量爬虫')
    ap.add_argument('--apk', help='战舰助手 APK 路径（用于自动提取并验证最新常量）')
    ap.add_argument('--out', default=os.path.dirname(os.path.abspath(__file__)),
                    help='输出目录')
    ap.add_argument('--index', help='要更新的 ship_names_zh.json 路径')
    ap.add_argument('--update-index', action='store_true',
                    help='爬完后自动更新助手项目的 ship_names_zh.json')
    ap.add_argument('--no-review', action='store_true', help='不抓 AI 评价（更快）')
    ap.add_argument('--probe-only', action='store_true', help='只探测接口，不爬全量')
    ap.add_argument('--check', action='store_true',
                    help='体检：1 秒看清接口状态与本地索引是否落后，不爬全量')
    ap.add_argument('--page-size', type=int, default=20)
    ap.add_argument('--workers', type=int, default=8)
    ap.add_argument('--limit', type=int, default=0, help='只爬前 N 艘（调试用）')
    a = ap.parse_args(argv[1:])

    default_index = r'C:\Users\mao_z\Downloads\WoWSBattleAssistant\Tools\ship_names_zh.json'
    # 默认就指向助手项目的索引：--check 只读不写，所以给默认值是安全的
    index_path = a.index or default_index
    do_write_index = bool(a.update_index or a.index)

    cand = {}
    if a.apk and os.path.exists(a.apk):
        cand = extract_from_apk(a.apk)
    elif a.apk:
        print('[!] APK 不存在：%s（改用内置常量）' % a.apk, file=sys.stderr)
    else:
        print('[*] 使用内置常量 —— 不需要 APK。'
              '（只有将来签名失效时才需要新 APK，届时脚本会明确提示）')

    paths = resolve_paths(cand)
    print('[*] 接口路径：列表=%s  详情=%s  评价=%s'
          % (paths['list'], paths['detail'], paths['comment']))

    api, total, how = probe_config(cand, paths['list'])
    if api is None:
        print('\n[×] 未能找到可用配置：%s' % how)
        if '签名被服务端拒绝' in how:
            print()
            print('    这是唯一需要 APK 的场景：')
            print('      1. 去 360 战舰助手官网下载最新 APK')
            print('      2. python wows_ship_scraper.py --apk <APK路径> --update-index')
            print('    脚本会自动从新 APK 里挖出并验证新常量。')
        else:
            print('    请检查网络 / 代理，或稍后重试（--check 可随时复检）。')
        return 2

    print('[*] 服务端报告共 %d 艘' % total)
    if a.check:
        return do_check(api, total, index_path)
    if a.probe_only:
        print('[*] --probe-only：探测完成，接口可用，未爬全量。')
        return 0

    ships, total = scrape(api, paths, want_review=not a.no_review,
                          detail_workers=a.workers, page_size=a.page_size,
                          limit=a.limit)
    if not ships:
        print('[×] 没有爬到任何舰船')
        return 3

    path = write_db(ships, a.out)
    size = os.path.getsize(path) / 1024.0 / 1024.0
    print('\n[3/3] 已写出数据库：%s（%.1f MB，%d 艘）' % (path, size, len(ships)))

    # 类型分布
    tc = {}
    for s in ships:
        tc[s.get('vtype') or '?'] = tc.get(s.get('vtype') or '?', 0) + 1
    print('      类型分布：' + '  '.join('%s %d' % (k, v) for k, v in
                                    sorted(tc.items(), key=lambda kv: -kv[1])))

    new_idx = build_index(ships)
    print('      索引条目：%d' % len(new_idx))

    if index_path and do_write_index:
        d = diff_index(index_path, new_idx)
        if d:
            print('\n      与旧索引对比（旧 %d 条）：' % d['old_count'])
            print('        新增 %d 艘' % len(d['added']))
            for k in d['added'][:15]:
                print('          + %s  %s' % (k, new_idx[k]))
            if len(d['added']) > 15:
                print('          … 还有 %d 艘' % (len(d['added']) - 15))
            if d['renamed']:
                print('        改名 %d 艘：' % len(d['renamed']))
                for k, o, n in d['renamed'][:10]:
                    print('          ~ %s  %s -> %s' % (k, o, n))
            if d['removed']:
                print('        消失 %d 艘（可能已下架）：%s'
                      % (len(d['removed']), d['removed'][:8]))
        try:
            os.makedirs(os.path.dirname(index_path), exist_ok=True)
            with open(index_path, 'w', encoding='utf-8') as f:
                json.dump(new_idx, f, ensure_ascii=False, separators=(',', ':'))
            print('\n[√] 已更新助手知识库：%s' % index_path)
        except Exception as e:
            print('[!] 更新索引失败：%s' % e, file=sys.stderr)
            return 4

    print('\n[√] 完成。共 %d 艘，请求 %d 次。' % (len(ships), api.nreq))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
