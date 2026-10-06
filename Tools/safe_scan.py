#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
safe_scan.py —— 通过 x64dbg MCP 端点分块扫内存，**不会锁死 HTTP 服务**。

【为什么要这个】
x64dbg 的 MCP 插件是**单线程同步**的：一次 `PatternFindMem` 请求会一直
占着连接直到扫完。我第一次调 `PatternFindMem(start=0, size=1GB)` 直接把
服务堵死 15 分钟，连 `/Breakpoint/List` 都超时。
→ 所以这里直接用 HTTP 自己控制：每块≤4MB、块间 sleep、
   带超时与重试，绝不让插件被单个请求压住。

用法：
  python safe_scan.py --pid 21004            # 自动分块扫，输出解密字符串
  python safe_scan.py --chunk 2              # 更小更稳（2MB/块）
"""
import argparse
import re
import sys
import time

try:
    import requests
except ImportError:
    print('需要 requests：pip install requests')
    sys.exit(1)

BASE = 'http://127.0.0.1:8888'
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

PATTERNS = {
    'URL': rb'https?://[\x20-\x7e]{6,300}',
    '域名': rb'[\w\-\.]{2,40}\.(?:com|net|cn|org|io|cc|top|xyz|ru|tk|space)\b',
    '路径': rb'[A-Za-z]:\\[\x20-\x7e]{4,200}',
    '注册表': rb'(?:HKEY_|SOFTWARE\\\\|CurrentVersion)[\x20-\x7e]{4,200}',
    '预瞄': rb'(?:autoaim|aimHeight|distDes|shotPoint|showLock|lockLine|'
            rb'torpedo|WorldOfWarships|wows\.exe)[\x20-\x7e]{0,60}',
    '授权': rb'(?:license|activation|verify|machinecode|hwid|token|auth|'
            rb'card|key)[-_ ]?[\x20-\x7e]{0,50}',
}


def get(path, **kw):
    return requests.get(BASE + path, timeout=kw.pop('timeout', 8), **kw)


def post(path, **kw):
    return requests.post(BASE + path, timeout=kw.pop('timeout', 8), **kw)


# start.exe 的基址/大小：x64dbg 每次加载 ASLR 都会变，这里从断点列表反推
START_BASE = 0
START_SIZE = 0x222a000


def discover_base():
    """从断点列表里找 start.exe 的入口断点，再减去 0x143cb3 得基址。"""
    global START_BASE
    try:
        bps = get('/Breakpoint/List').json().get('breakpoints') or []
    except Exception:
        return False
    for b in bps:
        if (b.get('module') or '').lower() == 'start.exe' and b.get('addr'):
            a = int(b['addr'], 16)
            START_BASE = a - 0x143cb3      # 入口 RVA
            return True
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--chunk-mb', type=float, default=4.0)
    ap.add_argument('--sleep', type=float, default=0.25)
    ap.add_argument('--skip-modules', action='store_true',
                    help='跳过主模块（只扫堆）')
    a = ap.parse_args()

    # 健康检查
    try:
        bps = get('/Breakpoint/List').json()
    except Exception as e:
        print('[×] MCP 服务不可用：%s' % str(e)[:80])
        print('    请确认 x64dbg 已启动且 Plugins → httpserver 已开启')
        return 2
    discover_base()
    print('[√] MCP 服务正常，当前断点 %d 个' % bps.get('count', 0))

    # 实测：/Module/List 这个端点不存在（连接被断开），但 /Breakpoint/List
    # 里能看到 module 名，只是拿不到 base。所以从入口断点反推基址
    # （start.exe 入口 RVA = 0x143cb3，之前在 IDA 里核过）。
    if not START_BASE:
        print('[!] 断点列表里找不到 start.exe 入口断点，无法定位基址。')
        print('    请确认 x64dbg 已加载 Start.exe，且 Plugins → httpserver 已开。')
        return 3
    print('[√] start.exe 基址 0x%x（由入口断点反推）' % START_BASE)
    regions = [(START_BASE, START_SIZE, 'start.exe')]

    chunk = int(a.chunk_mb * 1024 * 1024)
    hits = {k: {} for k in PATTERNS}
    done = 0
    t0 = time.time()

    for base, size, name in regions:
        # 大模块只扫前 24MB（代码段后面才是数据段/字符串区）
        limit = min(size, 24 * 1024 * 1024)
        print('\n[扫] %-22s %s  取前 %.1f MB' %
              (name, m['base'], limit / 1048576))
        off = 0
        while off < limit:
            n = min(chunk, limit - off)
            addr = base + off
            try:
                r = get('/Memory/Read', params={'addr': hex(addr), 'size': str(n)})
                txt = r.text.strip().strip('"')
                if not txt or txt.lower().startswith('error'):
                    off += n
                    continue
                raw = bytes.fromhex(txt.replace(' ', ''))
            except Exception:
                off += n          # 读不到就跳过这块
                time.sleep(a.sleep)
                continue
            for label, pat in PATTERNS.items():
                for mt in re.finditer(pat, raw):
                    try:
                        s = mt.group(0).decode('utf-8', 'replace')
                    except Exception:
                        continue
                    hits[label].setdefault(s, addr + mt.start())
            done += n
            off += n
            time.sleep(a.sleep)
            # 每 8MB 探一次健康，避免服务被压死
            if done % (8 * 1024 * 1024) == 0:
                try:
                    get('/Breakpoint/List', timeout=4)
                except Exception:
                    print('    [!] 服务暂时无响应，放慢…')
                    time.sleep(2)

    print('\n扫描完成：%.1f MB，耗时 %.0f 秒' % (done / 1048576, time.time() - t0))
    for label in PATTERNS:
        lst = hits[label]
        print('\n=== %s：%d 条 ===' % (label, len(lst)))
        for s, addr in list(lst.items())[:40]:
            print('  0x%x  %s' % (addr, s[:150]))
        if not lst:
            print('  （无）')
    return 0


if __name__ == '__main__':
    sys.exit(main())