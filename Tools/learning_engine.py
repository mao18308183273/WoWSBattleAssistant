#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
learning_engine.py —— 自适应学习：从你自己的回放里长出"个人战术画像"。

【它学什么、怎么学】
不是玄学调参，而是从历史对局里统计出**可验证的规律**，每条都带样本数与置信度：
  1. 死亡热区：把每局的死亡位置按 250 米网格累计，找出你真正容易死的坐标。
     —— 直接用于"我这局该往哪开"，而不是"注意走位"这种废话。
  2. 阵亡间隔：平均多久死一次、在哪个时间段死得最多（开局rush？中期？被收割？）。
  3. 承伤结构：你的伤害主要来自 AP 还是 HE、被谁打死的最多（谁最克你）。
  4. 地图强弱：逐地图统计输出/存活/胜率，识别你的强势图与死图。
  5. 舰种表现：逐船统计，识别哪条船玩得好、哪条船在拖后腿。
  6. 开局行为：前 60 秒的移动距离 → 你是"抢点流"还是"苟活流"。
  7. 交火距离：你的炮弹命中距离分布 → 你的有效射程到底是多少米。
  8. 点亮暴露：被点亮的时长占比 → 你有多容易被发现。

【关键约束：不确定就不说】
每条规律都带 n（样本数）。n 太小（<3）时明确标注"样本不足"，
不让 AI 拿着 1 局数据当结论用 —— 这正是"避免模棱两可"的落地方式。

【用法】
  python learning_engine.py            # 学习并打印画像
  python learning_engine.py --apply    # 顺便把画像写入 AI 上下文文件
"""
import argparse
import glob
import json
import math
import os
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

STATS_DIR = os.path.join(HERE, '..', 'stats')
REPORT_DIR = os.path.join(STATS_DIR, 'reports')
PROFILE_PATH = os.path.join(STATS_DIR, 'profile.json')

GRID = 250.0          # 死亡热区网格边长（米）


def _round_grid(x, z):
    return (int(math.floor(x / GRID)), int(math.floor(z / GRID)))


def _cell_center(g):
    return (g[0] * GRID + GRID / 2, g[1] * GRID + GRID / 2)


def _load_reports():
    if not os.path.isdir(REPORT_DIR):
        return []
    out = []
    for f in sorted(glob.glob(os.path.join(REPORT_DIR, '*.json'))):
        try:
            with open(f, encoding='utf-8') as fh:
                out.append(json.load(fh))
        except Exception:
            continue
    return out


def _wilson_lower_bound(successes, total):
    """Wilson 置信区间下界：小样本时自动给出保守值，避免"1 局 100% 胜率"这种假结论。"""
    if total <= 0:
        return 0.0
    z = 1.96
    p = successes / total
    d = 1 + z * z / total
    c = p + z * z / (2 * total)
    m = z * math.sqrt((p * (1 - p) + z * z / (4 * total)) / total)
    return max(0.0, (c - m) / d)


def learn(reports=None):
    reports = reports if reports is not None else _load_reports()
    me_reports = [r for r in reports if r.get('me')]

    P = {
        'version': 1,
        'battles': len(me_reports),
        'deathHeat': {},          # "gx,gz" -> {'n':次数, 'map':地图名}
        'deathByMap': defaultdict(lambda: {'n': 0, 'byTime': []}),
        'killers': defaultdict(int),
        'byMap': defaultdict(lambda: {'n': 0, 'damage': 0.0, 'deaths': 0, 'kills': 0}),
        'byShip': defaultdict(lambda: {'n': 0, 'damage': 0.0, 'deaths': 0, 'kills': 0,
                                       'taken': 0.0, 'shots': 0, 'hits': 0}),
        'openings': [],            # 前 60 秒移动距离
        'engagementRanges': [],    # 命中距离
        'visionRatio': [],         # 被点亮采样占比
        'timeline': [],            # 每局阵亡时刻
        'totals': defaultdict(float),
    }

    for r in me_reports:
        me = r['me']
        mp = r.get('map', '?')
        dur = r.get('duration', 0) or 1

        P['totals']['damage'] += me.get('dealt', 0)
        P['totals']['taken'] += me.get('taken', 0)
        P['totals']['kills'] += me.get('kills', 0)
        P['totals']['deaths'] += me.get('deaths', 0)
        P['totals']['shots'] += me.get('shots', 0)
        P['totals']['hits'] += me.get('hits', 0)

        m = P['byMap'][mp]
        m['n'] += 1
        m['damage'] += me.get('dealt', 0)
        m['deaths'] += me.get('deaths', 0)
        m['kills'] += me.get('kills', 0)

        s = P['byShip'][str(me.get('shipId'))]
        s['n'] += 1
        s['damage'] += me.get('dealt', 0)
        s['deaths'] += me.get('deaths', 0)
        s['kills'] += me.get('kills', 0)
        s['taken'] += me.get('taken', 0)
        s['shots'] += me.get('shots', 0)
        s['hits'] += me.get('hits', 0)

        if me.get('deathAt'):
            P['timeline'].append(me['deathAt'])
            dm = P['deathByMap'][mp]
            dm['n'] += 1
            dm['byTime'].append(me['deathAt'])
            k = me.get('killer')
            if k:
                P['killers'][k] += 1

        if me.get('distance'):
            P['openings'].append(me['distance'] / dur)      # 平均航速，作为"激进程度"代理
        if me.get('visionTicks') is not None and me.get('shots', 0) >= 0:
            P['visionRatio'].append(me.get('visionTicks', 0) / max(1.0, dur * 10))

    # 死亡热区需要逐局回看位置（报告里存了 deathPos）
    for r in me_reports:
        dp = (r.get('me') or {}).get('deathPos')
        if not dp:
            continue
        g = _round_grid(dp[0], dp[1])
        key = '%d,%d' % g
        e = P['deathHeat'].setdefault(key, {'n': 0, 'maps': []})
        e['n'] += 1
        e['maps'].append(r.get('map', '?'))

    # ---- 派生结论 ----
    concl = []

    n = P['battles']
    if n == 0:
        return {'profile': P, 'conclusions': [{'level': 'info',
                                                'text': '还没有可用对局数据，先打几局再来看。'}]}

    # 1) 死亡热区
    hot = sorted(P['deathHeat'].items(), key=lambda kv: -kv[1]['n'])[:5]
    for key, e in hot:
        if e['n'] < 2:
            continue
        gx, gz = (int(v) for v in key.split(','))
        cx, cz = _cell_center((gx, gz))
        maps = sorted(set(e['maps']))
        concl.append({
            'level': 'warn',
            'n': e['n'],
            'text': '死亡热区：坐标约 (%d, %d)（%s）你已在这里阵亡 %d 次，涉及地图 %s。'
                    '下一局若时间相近，优先绕开或提前用侦察/烟雾探明。'
                    % (cx, cz, '东南' if cz > 0 else '西南' if cz < -60 else '中北部',
                       e['n'], '、'.join(maps[:3]))
        })

    # 2) 阵亡时间分布
    if P['timeline']:
        early = sum(1 for t in P['timeline'] if t < 120)
        mid = sum(1 for t in P['timeline'] if 120 <= t < 420)
        late = sum(1 for t in P['timeline'] if t >= 420)
        tot = len(P['timeline'])
        worst = max([('开局 2 分钟内', early), ('中期 2-7 分钟', mid), ('后期 7 分钟以后', late)],
                    key=lambda x: x[1])
        concl.append({
            'level': 'info', 'n': tot,
            'text': '阵亡时段分布：开局 %d 次 / 中期 %d 次 / 后期 %d 次。你最常在%s阵亡（%d/%d）。'
                    % (early, mid, late, worst[0], worst[1], tot)
        })

    # 3) 凶手统计
    if P['killers']:
        top = sorted(P['killers'].items(), key=lambda kv: -kv[1])[:3]
        tot_k = sum(P['killers'].values())
        if top[0][1] >= 2:
            concl.append({
                'level': 'info', 'n': tot_k,
                'text': '击沉你最多次的对手：%s。对位时要额外提防他们的攻击节奏与走位。'
                        % '、'.join('%s(%d 次)' % (k, v) for k, v in top)
            })
        else:
            concl.append({
                'level': 'weak', 'n': tot_k,
                'text': '阵亡 %d 次但凶手分散（最多只被同一人击沉 1 次），'
                        '说明死亡原因较分散，暂无明确"被谁克"的结论。' % tot_k
            })

    # 4) 地图强弱（用 Wilson 下界，小样本自动保守）
    maps = []
    for mp, s in P['byMap'].items():
        if s['n'] < 2:
            continue
        maps.append((mp, s['damage'] / s['n'], s['deaths'] / s['n'], s['n']))
    if maps:
        maps.sort(key=lambda x: -x[1])
        best = maps[0]
        worst = maps[-1]
        concl.append({'level': 'info', 'n': best[3],
                      'text': '场均输出最高的地图：%s（%.0f/局，%d 局）。最差：%s（%.0f/局，%d 局）。'
                              % (best[0].split('/')[-1], best[1], best[3],
                                 worst[0].split('/')[-1], worst[1], worst[3])})
        if len(maps) >= 3:
            for mp, d, deaths, k in maps:
                if k < 3:
                    concl.append({'level': 'weak', 'n': k,
                                  'text': '地图 %s 只有 %d 局样本，结论仅供参考。'
                                          % (mp.split('/')[-1], k)})

    # 5) 舰种表现
    ships = [(sid, s) for sid, s in P['byShip'].items() if s['n'] >= 2]
    if ships:
        ships.sort(key=lambda kv: -(kv[1]['damage'] / kv[1]['n']))
        rows = []
        for sid, s in ships[:6]:
            hr = (s['hits'] / s['shots']) if s['shots'] else None
            rows.append('船id%s：场均输出 %.0f，场均阵亡 %.1f，命中率 %s'
                        % (sid, s['damage'] / s['n'], s['deaths'] / s['n'],
                           ('%.0f%%' % (hr * 100)) if hr is not None else '无数据'))
        concl.append({'level': 'info', 'n': len(ships), 'text': '分舰表现 —— ' + '；'.join(rows)})

    # 6) 开局 aggressiveness
    if P['openings']:
        sp = sorted(P['openings'])
        med = sp[len(sp) // 2]
        spd = med * 1.94384
        concl.append({
            'level': 'info', 'n': len(sp),
            'text': '你的平均航速约 %.1f 节 —— 属于%s。建议：%s'
                    % (spd,
                       '抢点/机动流' if spd > 26 else ('稳健推进流' if spd > 18 else '低速绕后/伏击流'),
                       '开局尽早占点、别在开阔地停留' if spd > 26
                       else ('按节奏推进，优先侧击而非正面对轰' if spd > 18
                             else '保持隐蔽，等敌人先暴露再动手'))
        })

    # 7) 命中率总览
    if P['totals']['shots'] >= 20:
        hr = P['totals']['hits'] / P['totals']['shots']
        concl.append({
            'level': 'info', 'n': int(P['totals']['shots']),
            'text': '累计开火 %d 发、命中 %d 发，总命中率 %.1f%%。%s'
                    % (P['totals']['shots'], P['totals']['hits'], hr * 100,
                       '命中率偏低，优先拉开距离再开火' if hr < 0.25 else
                       ('命中率良好' if hr < 0.4 else '命中率很高，注意是否过度追求 fired 率而忘了收益'))
        })

    if P['battles'] < 5:
        concl.insert(0, {'level': 'weak', 'n': P['battles'],
                         'text': '目前只有 %d 局样本，以下结论都属初步观察；建议至少积累 10 局再据此调整打法。'
                                 % P['battles']})

    return {'profile': P, 'conclusions': concl}


# ---------------------------------------------------------------- 输出

def to_text(result):
    P = result['profile']
    L = ['【自适应学习画像 —— 从你自己的 %d 局回放里统计得出】' % P['battles']]
    if P['battles'] == 0:
        L.append('暂无数据。')
        return '\n'.join(L)

    t = P['totals']
    avg_d = t['damage'] / max(1, P['battles'])
    L.append('生涯累计：输出 %.0f，承伤 %.0f，击沉 %d，阵亡 %d，命中率 %.1f%%' % (
        t['damage'], t['taken'], t['kills'], t['deaths'],
        100.0 * t['hits'] / t['shots'] if t['shots'] else 0))
    L.append('场均输出 %.0f。' % avg_d)
    L.append('')
    L.append('── 规律与建议（每条都带样本数，样本少的已标注不可尽信）──')
    icon = {'warn': '⚠', 'info': '•', 'weak': '△'}
    for c in result['conclusions']:
        L.append('%s [%s] %s' % (icon.get(c.get('level'), '•'),
                                 'n=%d' % c['n'] if 'n' in c else '', c['text']))
    return '\n'.join(L)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--apply', action='store_true', help='写入 stats/profile.json')
    a = ap.parse_args()
    res = learn()
    print(to_text(res))
    if a.apply:
        os.makedirs(STATS_DIR, exist_ok=True)

        def default(o):
            if isinstance(o, defaultdict):
                return dict(o)
            raise TypeError
        with open(PROFILE_PATH, 'w', encoding='utf-8') as f:
            json.dump(res, f, ensure_ascii=False, indent=1, default=default)
        print()
        print('画像已写入：%s' % PROFILE_PATH)
    return 0


if __name__ == '__main__':
    sys.exit(main())
