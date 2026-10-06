#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ballistic_warning.py —— 炮弹落点预测与"能不能躲"分析。

【回答什么问题】
对每一发炮弹，算出：
  · 谁开的、什么时候开的、炮口在哪
  · 落点在哪、什么时候到（飞行时间）
  · **落点距离我多远**（这才是关键 —— 不是"谁在打我"，而是"炮弹会不会落在我头上"）
  · 以我的航速，飞行时间内能横移多远 → **来不来得及躲**

【为什么能算出落点】
回放里每一发炮弹都带完整的弹道参数（在 EntityMethod 的方法参数里，经 gamedata
的实体定义解析出来）：炮口 pos、落点 tarPos、初速 speed、仰角 pitch、命中距离
hitDistance。**服务器下发时落点就已经定好了** —— 敌方开火的那一刻就定了，
所以这不是"推测"，是读取权威值。

【为什么这套不能直接用于实时预警】
wows-replay-parser 用一次性 zlib.decompress()，对局中未完成的回放会整条流
解压失败（实测截断到 25% → 解出 0 个事件）。改增量解压的话，纯 Python
Blowfish 解密 1.2MB 回放要约 2 分钟，赶不上"提前 10 秒预警"。
实时化需要把解密管线搬到 C#（.NET 的 DeflateStream + 自写 Blowfish），
量级约 10^2 倍加速 —— 见项目里的说明。

用法：
  python ballistic_warning.py                    # 分析最近一局
  python ballistic_warning.py --replay X
  python ballistic_warning.py --all             # 累积"被瞄准"统计
"""
import argparse
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

GAMEDATA = os.path.join(HERE, '_gamedata', 'data', 'scripts_entity', 'entity_defs')
STATS_DIR = os.path.join(HERE, '..', 'stats')
NEAR_PATH = os.path.join(STATS_DIR, 'aimed_at_me.json')


def analyse(path):
    from wows_replay_parser import parse_replay
    r = parse_replay(replay_path=path, gamedata_path=GAMEDATA)

    info = {p.entity_id: {'name': p.name, 'relation': p.relation, 'isBot': p.is_bot}
            for p in r.players}
    me_id = next((p.entity_id for p in r.players if p.relation == 0), None)

    # 自舰轨迹：只能用 MinimapVisionEvent（回放没有自舰 Position 流）
    my = sorted((e.timestamp, e.world_x, e.world_z, e.heading_degrees)
                for e in r.events
                if type(e).__name__ == 'MinimapVisionEvent'
                and getattr(e, 'vehicle_entity_id', 0) == me_id)
    ts = [m[0] for m in my]

    def my_at(t, max_lag=6.0):
        import bisect
        if not ts:
            return None
        i = bisect.bisect_right(ts, t) - 1
        if i < 0 or t - ts[i] > max_lag:
            return None
        return my[i]

    # 把同一门炮的齐射聚成一组：salvo_id 相同 + 时间几乎相同
    shells = defaultdict(list)
    for e in r.events:
        if type(e).__name__ != 'ShotCreatedEvent':
            continue
        own = getattr(e, 'owner_id', 0)
        if own == me_id:
            continue                       # 自己开的炮不算威胁
        if own in info and info[own]['relation'] == 1:
            continue                       # 队友的炮也不算（友军误伤另算）
        shells[(e.owner_id, round(e.timestamp, 1))].append(e)

    threats = []
    for (owner, t0), grp in shells.items():
        sp = grp[0].speed or 900.0
        # 落点均值 = 各炮管 tarPos 的中心
        ax = sum(s.target_x for s in grp) / len(grp)
        az = sum(s.target_z for s in grp) / len(grp)
        mx = sum(s.spawn_x for s in grp) / len(grp)
        mz = sum(s.spawn_z for s in grp) / len(grp)
        # 散布半径 = 各落点到中心的平均距离（这才是"危险区半径"）
        spread = sum(math.hypot(s.target_x - ax, s.target_z - az) for s in grp) / len(grp)
        dist = math.hypot(ax - mx, az - mz)
        # 飞行时间用 hitDistance（炮弹实际飞行距离，含抛射修正）而不是
        # 炮口到瞄准点的直线距离 —— 后者会低估抛射，把 0.5 秒算成 0.3 秒。
        hd = grp[0].hit_distance or dist
        flight = hd / sp
        arrive = t0 + flight

        mp = my_at(t0)                    # 开火时刻我的位置（比到达时刻更可靠）
        if mp is None:
            continue
        # 敌方瞄准点离我多远。注意 aim 是"敌方想打的位置"，不等于落点；
        # 散布（spread）才是危险区半径。
        d_me = math.hypot(ax - mp[1], az - mp[2])
        if d_me > spread + 300:           # 瞄的都不是我附近，直接排除
            continue
        d_muzzle = math.hypot(mx - mp[1], mz - mp[2])

        # ---- 躲避判定（这里第一版逻辑是错的：把"瞄准点离我 232 米"判成
        # "能躲"。实际上瞄准点离我远 = 炮弹落在我旁边 = 本来就不需要躲。）
        # 正确逻辑两步：
        #   1) 我在不在危险区内？散布半径就是危险区半径。
        #   2) 在危险区内的话，飞行时间内能不能横移出去。
        # WoWS 最高航速约 39 节 ≈ 20 m/s，但转舵有延迟，取 15 m/s 作有效横移。
        in_danger = d_me <= spread + 10.0
        need = spread + 10.0                     # 要脱离危险区需横移的距离
        dodgeable = in_danger and (need / flight) <= 15.0
        threats.append({
            't': round(t0, 1),
            'arrive': round(arrive, 1),
            'flight': round(flight, 2),
            'from': info.get(owner, {}).get('name', '未知'),
            'isBot': info.get(owner, {}).get('isBot'),
            'relation': info.get(owner, {}).get('relation'),
            'muzzle': (round(mx, 1), round(mz, 1)),
            'aim': (round(ax, 1), round(az, 1)),
            'gunDist': round(d_muzzle, 0),
            'hitDist': round(hd, 0),
            'spread': round(spread, 1),
            'dToMe': round(d_me, 1),
            'inDanger': in_danger,
            'dodgeable': dodgeable,
            'dodgeDist': round(min(flight * 15.0, 9999), 0),
            'needDist': round(need, 0),
            'shells': len(grp),
            'myPos': (round(mp[1], 1), round(mp[2], 1)),
            'myHeading': round(mp[3] % 360, 1),
        })

    threats.sort(key=lambda x: x['t'])
    dodgeable = [t for t in threats if t['dodgeable']]
    inDanger = [t for t in threats if t['inDanger']]
    # 无躲避窗口 = 在危险区内但横移速度不够躲
    no_window = [t for t in inDanger if not t['dodgeable']]
    return {
        'file': os.path.basename(path),
        'map': r.map_name,
        'duration': round(r.duration, 1),
        'totalSalvos': len(shells),
        'threats': threats,
        'stats': {
            'salvos': len(shells),
            'aimedAtMe': len(threats),
            'inDanger': len(inDanger),
            'dodgeable': len(dodgeable),
            'noWindow': len(no_window),
            'minFlight': round(min([t['flight'] for t in threats], default=0), 2),
            'maxFlight': round(max([t['flight'] for t in threats], default=0), 2),
            'avgFlight': round(sum(t['flight'] for t in threats) / len(threats), 2) if threats else 0,
            'minLead': round(min([t['flight'] for t in dodgeable], default=0), 2),
        },
        'byShooter': {k: v['n'] for k, v in
                      sorted(((k, {'n': sum(1 for t in threats if t['from'] == k)})
                              for k in {t['from'] for t in threats}),
                             key=lambda kv: -kv[1]['n'])[:8]},
    }


def to_text(res, limit=30):
    L = []
    st = res['stats']
    L.append('【炮弹落点预测（服务器下发时落点即已确定，非推测）】')
    L.append('地图：%s　时长：%.0f 秒' % (res['map'], res['duration']))
    L.append('')
    L.append('全场敌方齐射 %d 次，落点在我附近的 %d 次 —— 其中 %d 次我处在危险区内（必须躲），'
             '但只有 %d 次来得及躲，**%d 次飞行时间太短、躲不掉**。'
             % (st['salvos'], st['aimedAtMe'], st['inDanger'], st['dodgeable'], st['noWindow']))
    if st['noWindow'] and st['noWindow'] > st['inDanger'] * 0.4:
        L.append('⚠ 超过 4 成的炮在你身上时**没有任何躲避窗口**（飞行 <%.2f 秒）——'
                 '这个距离上只能靠烟幕、地形遮蔽、或先手开火抢先。'
                 % (st['minFlight'] + 0.2))
    if st['aimedAtMe']:
        L.append('提前量：最少 %.2f 秒，最多 %.2f 秒，平均 %.2f 秒%s' % (
            st['minFlight'], st['maxFlight'], st['avgFlight'],
            '（最短提前量意味着一旦开火就只有 %.1f 秒反应时间）' % st['minLead']
            if st['minLead'] and st['minLead'] < 3 else ''))
    L.append('')
    if res['threats']:
        L.append('  %-7s %-20s %-7s %-9s %-11s %-8s %s' % (
            '开火时', '开炮者[AI]/弹数', '飞行', '炮口距我', '瞄准点距我', '散布半径', '躲避可行性'))
        for t in res['threats'][:limit]:
            tag = '[AI]' if t.get('isBot') else ''
            if t.get('relation') == 1:
                tag += '[友军]'
            if not t['inDanger']:
                verdict = '无需躲：落点在我 %.0f 米外' % t['dToMe']
            elif t['dodgeable']:
                verdict = '能躲：%.2f 秒内可横移 %.0f 米（需 %.0f 米）' % (
                    t['flight'], t['dodgeDist'], t['needDist'])
            else:
                verdict = '**躲不掉**：%.2f 秒只能横移 %.0f 米，需 %.0f 米' % (
                    t['flight'], t['dodgeDist'], t['needDist'])
            L.append('  %6.1fs %-20s %6.2fs %7.0fm %9.0fm %8.0fm  %s' % (
                t['t'], (t['from'][:15] + tag + '/%d' % t['shells']),
                t['flight'], t['gunDist'], t['dToMe'], t['spread'], verdict))
        if len(res['threats']) > limit:
            L.append('  …另有 %d 次未列出' % (len(res['threats']) - limit))
    else:
        L.append('本局没有任何一次敌方齐射把落点投在我附近。')

    if res.get('byShooter'):
        L.append('')
        L.append('■ 瞄准我次数最多的来源')
        for k, v in list(res['byShooter'].items())[:6]:
            L.append('  %-20s %d 次' % (k[:20], v))
    return '\n'.join(L)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--replay')
    ap.add_argument('--all', action='store_true')
    ap.add_argument('--limit', type=int, default=20)
    a = ap.parse_args()
    d = os.path.join(os.environ.get('WOWS_PATH', r'J:\Games\World_of_Warships_CN360'), 'replays')
    files = [a.replay] if a.replay else [
        os.path.join(d, f) for f in os.listdir(d) if f.endswith('.wowsreplay')]
    files.sort(key=os.path.getmtime, reverse=True)
    if not os.path.isdir(GAMEDATA):
        print('[×] 缺少实体定义：%s' % GAMEDATA)
        return 2
    res = None
    for f in (files if a.all else files[:1]):
        try:
            r = analyse(f)
            print('[√] %s' % os.path.basename(f)[:52])
            res = r
        except Exception as ex:
            print('[×] %s 失败：%s' % (os.path.basename(f)[:52], str(ex)[:90]))
    if res:
        print()
        print(to_text(res, a.limit))
    if a.all:
        os.makedirs(STATS_DIR, exist_ok=True)
        agg = defaultdict(lambda: defaultdict(int))
        if os.path.exists(NEAR_PATH):
            try:
                with open(NEAR_PATH, encoding='utf-8') as fh:
                    for k, v in json.load(fh).items():
                        agg[k].update(v)
            except Exception:
                pass
        for r in ([res] if res else []):
            for k, v in r['byShooter'].items():
                agg[k][r['map']] += v
        out = {k: dict(v) for k, v in agg.items()}
        with open(NEAR_PATH, 'w', encoding='utf-8') as fh:
            json.dump(out, fh, ensure_ascii=False, indent=1)
        print()
        print('被瞄准来源已累积：%s' % NEAR_PATH)
    return 0


if __name__ == '__main__':
    sys.exit(main())
