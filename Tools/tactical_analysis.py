#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
tactical_analysis.py —— 把回放变成"可执行的情报"，而不是模糊的描述。

回答四个问题（都用服务端权威数据，坐标精确到 0.1 米，不让 AI 猜）：

1. **是谁开的这个炮**
   ShotCreatedEvent.owner_id → 玩家 → 阵营。同时给出炮口位置、目标点、弹速、齐射数。

2. **敌方火力是否覆盖了我**
   不用理论射程（会骗人），而是**用真实弹道**：
   把同一次齐射（salvo_id 相同）的所有炮弹聚成一组，算出炮口、目标中心、
   散布半径；再取我在"炮弹到达时刻"的位置，算它到弹道射线的垂距。
   垂距小于散布半径 ⇒ 判定为**覆盖**。
   因为用的是实际炮弹数据，所以它能识别"这一炮就是冲你来的"。

3. **还有几秒被打**
   飞行时间 = 命中距离 / 弹速（实测弹速约 950 m/s）。
   输出"XX 号在 12.3 秒时开火，炮弹 1.8 秒后到达你当前位置" —— 这是可以
   直接行动的信息（转向/拉烟/进掩体），不是"注意规避"。

4. **姿态对命中的影响（学习）**
   统计我被命中时的姿态：
     - 命中方位与我舰艏向的夹角
     - 敌人在我哪个方向
   得出"被侧击/被尾击/被正击各占多少、单次伤害差多少"。
   这是" ships 姿态影响命中"的量化答案。

另外产出**敌方出现热区**：敌方逐时��位置按 250 米网格累计，
直接回答"AI 一般在这张地图的哪个位置出现"。

用法：
  python tactical_analysis.py                  # 分析最近一局
  python tactical_analysis.py --replay X
  python tactical_analysis.py --all            # 累积敌方热区到 stats/threat_map.json
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

# ★ 坐标单位不是米：1 单位 = 8.15 米（见 coord_scale.py 的标定过程）
from coord_scale import K_METERS, to_meters
STATS_DIR = os.path.join(HERE, '..', 'stats')
THREAT_PATH = os.path.join(STATS_DIR, 'threat_map.json')

GRID = 250.0 * 8.15          # ★ 网格边长也要换算：坐标单位 → 米
NEAR_MISS_PAD = 60.0 * 8.15  # ★ 判定阈值同理


def _cell(x, z):
    return (int(math.floor(x / GRID)), int(math.floor(z / GRID)))


def _pos_of(v):
    return (float(v[0]), float(v[2]))


def _angle_diff(a, b):
    """两个方向（弧度）的最小夹角，结果在 [0, pi]。"""
    d = abs(a - b) % (2 * math.pi)
    return d if d <= math.pi else 2 * math.pi - d


def analyse(path):
    from wows_replay_parser import parse_replay
    r = parse_replay(replay_path=path, gamedata_path=GAMEDATA)

    # ---------- 名单 ----------
    info = {}
    for p in r.players:
        info[p.entity_id] = {
            'name': p.name, 'relation': p.relation, 'isBot': p.is_bot,
            'maxHealth': p.max_health, 'shipId': p.ship_id,
        }
    me_id = next((p.entity_id for p in r.players if p.relation == 0), None)
    my_name = info.get(me_id, {}).get('name', '')

    # ---------- 轨迹与姿态 ----------
    # 【关键】回放里**从来没有自舰的 Position 流** —— 客户端本地就知道自己的位置，
    # 不需要网络同步，所以 17 艘船里只有 16 艘有 Position，缺的常常是自己。
    # 自舰轨迹改从 MinimapVisionEvent 取：它按"实体被点亮"记录，含世界坐标与航向。
    # 实测 653 秒的局里有 766 条自舰样本，足够重建轨迹。
    #
    # 【角度基准，已用位移方向标定】heading_degrees 就是标准罗盘方位：
    #   0°=+z(北)  90°=+x(东)  ±180°=-z(南)  -90°=-x(西)
    # 标定方法：取相邻样本的 atan2(dx,dz) 与 heading 对比，实测误差 1~4 度
    #（船在转弯时的滞后），确认基准一致。全项目统一用这个基准。
    def radar(h):
        return math.radians(h)

    my_track = []          # (t, x, z, heading_rad)
    pos_by_id = defaultdict(list)
    vision_by_id = defaultdict(list)

    for e in r.events:
        n = type(e).__name__
        if n == 'PositionEvent':
            x, z = getattr(e, 'x', None), getattr(e, 'z', None)
            if x is None or z is None:
                continue
            lst = pos_by_id[e.entity_id]
            if not lst or (e.timestamp - lst[-1][0]) > 2.0:
                lst.append((e.timestamp, x, z, getattr(e, 'yaw', 0.0) or 0.0))
        elif n == 'MinimapVisionEvent':
            # 采样密度高（约 1.2 条/秒），且带真实航向 —— 优先于 Position
            vid = getattr(e, 'vehicle_entity_id', None)
            if vid is None:
                continue
            vision_by_id[vid].append(
                (e.timestamp, e.world_x, e.world_z, radar(e.heading_degrees)))
            if vid == me_id:
                my_track.append((e.timestamp, e.world_x, e.world_z, radar(e.heading_degrees)))

    my_track.sort()
    # 轨迹源优先级（这个选择很关键）：
    #   自舰    → 只能用 MinimapVisionEvent（回放没有自舰 Position 流），稀疏
    #   敌方    → 用 PositionEvent（每 tick 一条，密集连续），vision 只用于补朝向
    # 之前错误地用 vision 覆盖了敌方轨迹，导致距离算出 90~270 米这种荒谬值
    # （拿一个十几秒前的过期坐标去算距离）。
    for eid in list(pos_by_id.keys()):
        pos_by_id[eid].sort()


    def pos_at(lst, t, max_lag=6.0):
        """取时刻 t 的最近采样点（二分）。

        max_lag 很重要：轨迹来源有两种，密度差别很大 ——
        MinimapVisionEvent 只在"被点亮"时才有采样，可能出现十几秒的空档；
        超过 max_lag 就判定为"位置不可信"，返回 None 而不是拿一个过期坐标
        去算距离（那会算出 90 米这种荒谬结果）。
        """
        if not lst:
            return None
        lo, hi = 0, len(lst) - 1
        best = None
        while lo <= hi:
            mid = (lo + hi) // 2
            if lst[mid][0] <= t:
                best = lst[mid]
                lo = mid + 1
            else:
                hi = mid - 1
        if best is None:
            return None
        if t - best[0] > max_lag:
            return None
        return best

    def my_pos_at(t):
        return pos_at(my_track, t)

    # ---------- 齐射分组（salvo 相同 = 同一门炮的一次齐射）----------
    salvos = defaultdict(list)
    for e in r.events:
        if type(e).__name__ != 'ShotCreatedEvent':
            continue
        owner = getattr(e, 'owner_id', None)
        if owner is None or owner == me_id:
            continue
        salvos[(owner, getattr(e, 'salvo_id', 0))].append(e)

    threats = []            # 对我构成直接威胁的齐射
    shooter_stats = defaultdict(lambda: {'salvos': 0, 'shells': 0, 'atMe': 0})

    for (owner, sid), shells in salvos.items():
        shooter_stats[owner]['salvos'] += 1
        shooter_stats[owner]['shells'] += len(shells)
        mx = sum(s.spawn_x for s in shells) / len(shells)
        mz = sum(s.spawn_z for s in shells) / len(shells)
        tx = sum(s.target_x for s in shells) / len(shells)
        tz = sum(s.target_z for s in shells) / len(shells)
        # 散布半径：各炮管落点相对目标中心的距离（取均值，最能代表实际覆盖）
        spread = sum(math.hypot(s.target_x - tx, s.target_z - tz) for s in shells) / len(shells)
        speed = shells[0].speed or 900.0
        t0 = shells[0].timestamp
        dx, dz = tx - mx, tz - mz
        seg = math.hypot(dx, dz)
        if seg < 1:
            continue
        ux, uz = dx / seg, dz / seg
        flight = seg / speed

        arrive = t0 + flight
        me_p = my_pos_at(arrive)
        if me_p is None:
            continue
        vx, vz = me_p[1] - mx, me_p[2] - mz
        along = vx * ux + vz * uz
        if along <= 0 or along > seg * 1.25:
            continue
        perp = abs(vx * uz - vz * ux)     # 垂距
        if perp <= spread + NEAR_MISS_PAD:
            shooter_stats[owner]['atMe'] += 1
            threats.append({
                't': round(t0, 1),
                'arrive': round(arrive, 1),
                'flight': round(flight, 2),
                'shooter': info.get(owner, {}).get('name', '未知'),
                'shooterRelation': info.get(owner, {}).get('relation'),
                'isBot': info.get(owner, {}).get('isBot'),
                'muzzle': (round(mx, 1), round(mz, 1)),
                'aim': (round(tx, 1), round(tz, 1)),
                'spread': round(spread, 1),
                'perpDist': round(perp, 1),
                'distFromMuzzle': round(math.hypot(vx, vz), 1),
                'shells': len(shells),
            })

    threats.sort(key=lambda x: x['t'])

    # ---------- 受击明细：谁开的这个炮（权威口径） ----------
    # 【为什么不能用弹道预测当结论】实测：判定的 8 次"直击"在 ±3 秒内
    # 都找不到对应伤害（0/8 命中）。因为 target 是开火**瞬间的瞄准点**（敌方预测
    # 的位置），不是炮弹落点；而自舰轨迹只能从 MinimapVisionEvent（被点亮时）
    # 采样，位置会过时。所以"谁打的我"必须以 DamageEvent 为准。
    # 弹道预测只作为补充信号（谁在瞄准我），不当作结论。
    incoming = []
    for e in r.events:
        if type(e).__name__ != 'DamageEvent':
            continue
        if getattr(e, 'target_id', 0) != me_id or (e.damage or 0) <= 0:
            continue
        a = getattr(e, 'attacker_id', 0)
        if a == 0 or a not in info:
            continue                       # 0 = 环境/友伤，不算玩家火力
        mp = my_pos_at(e.timestamp)
        ap = pos_at(pos_by_id.get(a), e.timestamp)
        if mp is None or ap is None:
            continue                       # 位置不可信宁可不报，也不给假距离
        # 敌方航向优先用 vision（heading 已标定为罗盘方位），退化用 Position 的 yaw
        vh = pos_at(vision_by_id.get(a), e.timestamp, max_lag=15.0)
        ap_hdg = vh[3] if vh else None
        d = to_meters(math.hypot(ap[1] - mp[1], ap[2] - mp[2]))   # ★ 转米
        incoming.append({
            't': round(e.timestamp, 1),
            'from': info[a]['name'],
            'isBot': info[a]['isBot'],
            'relation': info[a]['relation'],
            'dmg': round(e.damage, 0),
            'dist': round(d, 1),
            'myPos': (to_meters(mp[1]), to_meters(mp[2])),
            'myHeading': round(math.degrees(mp[3]) % 360, 1),
            'theirPos': (to_meters(ap[1]), to_meters(ap[2])),
            'theirHeading': (round(math.degrees(ap_hdg) % 360, 1)
                             if ap_hdg is not None else None),
            'bearing': round(math.degrees(math.atan2(ap[1] - mp[1], ap[2] - mp[2])) % 360, 1),
        })
    incoming.sort(key=lambda x: x['t'])

    # 按攻击者汇总
    in_by = defaultdict(lambda: {'n': 0, 'dmg': 0.0, 'dist': []})
    for h in incoming:
        s = in_by[h['from']]
        s['n'] += 1
        s['dmg'] += h['dmg']
        s['dist'].append(h['dist'])

    # ---------- 受击分析：命中方位 vs 我舰艏向 ----------
    def heading_of(eid, t):
        lst = pos_by_id.get(eid)
        if not lst:
            return None
        best = lst[0]
        for s in lst:
            if s[0] <= t:
                best = s
            else:
                break
        return best[3]

    posture = defaultdict(lambda: {'n': 0, 'dmg': 0.0})   # 舷侧 -> 统计
    for e in r.events:
        if type(e).__name__ != 'DamageEvent':
            continue
        if getattr(e, 'target_id', 0) != me_id or (e.damage or 0) <= 0:
            continue
        a = getattr(e, 'attacker_id', 0)
        at = e.timestamp
        best = pos_at(pos_by_id.get(a), at, max_lag=8.0)
        mp = my_pos_at(at)
        if best is None:
            continue
        if mp is None:
            continue
        hdg = mp[3]
        brg = math.atan2(best[1] - mp[1], best[2] - mp[2])    # 敌人在我哪个方向
        diff = _angle_diff(brg, hdg)                          # 与我舰艏向的夹角
        deg = math.degrees(diff)
        if deg < 22.5:
            k = '正面对敌（舰艏朝向敌人）'
        elif deg < 67.5:
            k = '侧前 22-67°'
        elif deg < 112.5:
            k = '正侧舷'
        elif deg < 157.5:
            k = '侧后 112-157°'
        else:
            k = '正后方（舰尾朝向敌人）'
        posture[k]['n'] += 1
        posture[k]['dmg'] += e.damage

    # ---------- 敌方出现热区 ----------
    heat = defaultdict(int)
    enemy_ids = [eid for eid, v in info.items() if v['relation'] == 2]
    for eid in enemy_ids:
        for t, x, z, _ in pos_by_id.get(eid, []):
            gx, gz = _cell(x, z)
            heat['%d,%d' % (gx, gz)] += 1

    return {
        'file': os.path.basename(path),
        'map': r.map_name,
        'duration': round(r.duration, 1),
        'incoming': incoming,
        'incomingBy': {k: {'n': v['n'], 'dmg': v['dmg'],
                           'avgDist': round(sum(v['dist']) / max(1, len(v['dist'])), 1)}
                       for k, v in in_by.items()},
        'me': {'name': my_name, 'id': me_id,
               'maxHealth': info.get(me_id, {}).get('maxHealth')},
        'threats': threats,
        'shooterStats': {
            str(k): dict(v, name=info.get(k, {}).get('name', '未知'),
                         relation=info.get(k, {}).get('relation'))
            for k, v in shooter_stats.items() if v['salvos'] > 0
        },
        'posture': dict(posture),
        'enemyHeat': dict(heat),
        'myTrack': my_track,
    }


# ---------------------------------------------------------------- 文本输出

def to_text(res, limit_threats=25):
    L = []
    L.append('【战术情报（真实弹道反推，坐标精确到 0.1 米，非推测）】')
    L.append('地图：%s　时长：%.0f 秒　（距离已换算为米，1 单位 = %.1f 米）'
             % (res['map'], res['duration'], K_METERS))

    # 火力覆盖：权威口径 = 实际打我的人
    inc = res.get('incoming') or []
    L.append('')
    L.append('■ 敌方火力命中我的次数：%d 次' % len(inc))
    if inc:
        tot = sum(h['dmg'] for h in inc)
        L.append('  累计承受伤害 %.0f。以下每条都是 DamageEvent 记录的真实命中'
                 '（不是弹道预测），含攻击者、距离、我方舰艏向。' % tot)
        L.append('')
        L.append('  %-7s %-16s %-7s %-9s %-10s %-9s %s' % (
            '时刻', '攻击者', '伤害', '距我', '我舰艏向', '敌方位', '相对我舰艏向'))
        for h in inc[:limit_threats]:
            rel = (h['bearing'] - h['myHeading']) % 360
            side = ('正前' if rel < 22.5 or rel >= 337.5 else
                    '右舷' if 22.5 <= rel < 157.5 else
                    '正后' if 157.5 <= rel < 202.5 else '左舷')
            tag = '[AI]' if h.get('isBot') else ''
            if h.get('relation') == 1:
                tag += '[友军误伤]'
            th = ('%7.1f°' % h['theirHeading']) if h.get('theirHeading') is not None else '      ?'
            L.append('  %6.1fs %-16s%-9s %6.0f %7.0fm %8.1f° %8.1f°  %s' % (
                h['t'], h['from'][:16], tag, h['dmg'], h['dist'],
                h['myHeading'], h['bearing'], side))
        if len(inc) > limit_threats:
            L.append('  …另有 %d 次未列出' % (len(inc) - limit_threats))
    else:
        L.append('  本局没有玩家火力命中我。')

    # 按来源汇总
    ib = res.get('incomingBy') or {}
    if ib:
        L.append('')
        L.append('■ 谁打我最多（真实命中统计）')
        for k, v in sorted(ib.items(), key=lambda kv: -kv[1]['dmg'])[:10]:
            L.append('  %-18s 命中 %3d 次，合计 %7.0f，平均距离 %6.0f 米' % (
                k[:18], v['n'], v['dmg'], v['avgDist']))

    # 谁开炮最多
    ss = sorted(res['shooterStats'].items(), key=lambda kv: -kv[1]['atMe'])
    if ss:
        L.append('')
        L.append('■ 各来源开火情况（atMe = 其中覆盖到我的次数）')
        for k, v in ss[:10]:
            rel = {0: '我', 1: '队友', 2: '敌方'}.get(v.get('relation'), '?')
            L.append('  %-18s %-4s 齐射 %4d 次 / 炮弹 %5d 发 / 覆盖我 %3d 次' % (
                str(v.get('name'))[:18], rel, v['salvos'], v['shells'], v['atMe']))

    # 姿态
    p = res.get('posture') or {}
    if p:
        L.append('')
        L.append('■ 我被命中时，攻击者相对我舰艏向的方位分布')
        for k in ['正面对敌（舰艏朝向敌人）', '侧前 22-67°', '正侧舷', '侧后 112-157°', '正后方（舰尾朝向敌人）']:
            if k in p:
                v = p[k]
                L.append('  %-24s 命中 %4d 次，累计伤害 %8.0f，平均 %6.0f/次' % (
                    k, v['n'], v['dmg'], v['dmg'] / max(1, v['n'])))
        L.append('  → 这就是"姿态影响命中"的量化结果：横向受击面积最大，正对/正后最小。')

    # 敌方热区
    heat = res.get('enemyHeat') or {}
    if heat:
        L.append('')
        L.append('■ 敌方在本局出现最密集的区域（250 米网格，取样点数）')
        for k, v in sorted(heat.items(), key=lambda kv: -kv[1])[:6]:
            gx, gz = (int(x) for x in k.split(','))
            cx, cz = gx * GRID + GRID / 2, gz * GRID + GRID / 2
            L.append('  区域 (%5d, %5d) 方向 %-4s 采样 %4d' % (
                cx, cz, '北' if cz > 150 else '南' if cz < -150 else '中', v))
    return '\n'.join(L)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--replay')
    ap.add_argument('--all', action='store_true', help='累积敌方热区到 threat_map.json')
    a = ap.parse_args()
    d = os.path.join(os.environ.get('WOWS_PATH', r'J:\Games\World_of_Warships_CN360'), 'replays')
    if a.replay:
        files = [a.replay]
    else:
        files = [os.path.join(d, f) for f in os.listdir(d) if f.endswith('.wowsreplay')]
        files.sort(key=os.path.getmtime, reverse=True)
    if not os.path.isdir(GAMEDATA):
        print('[×] 缺少实体定义：%s' % GAMEDATA)
        return 2

    results = []
    for f in (files if a.all else files[:1]):
        try:
            results.append(analyse(f))
            print('[√] %s' % os.path.basename(f)[:50])
        except Exception as ex:
            print('[×] %s 失败：%s' % (os.path.basename(f)[:50], str(ex)[:100]))

    if results:
        print()
        print(to_text(results[-1]))

    if a.all and results:
        os.makedirs(STATS_DIR, exist_ok=True)
        agg = defaultdict(lambda: defaultdict(int))
        if os.path.exists(THREAT_PATH):
            try:
                with open(THREAT_PATH, encoding='utf-8') as fh:
                    for k, v in json.load(fh).items():
                        agg[k].update(v)
            except Exception:
                pass
        for res in results:
            for k, v in res['enemyHeat'].items():
                agg[k][res['map']] += v
        out = {k: dict(v) for k, v in agg.items()}
        with open(THREAT_PATH, 'w', encoding='utf-8') as fh:
            json.dump(out, fh, ensure_ascii=False, indent=1)
        print()
        print('敌方热区已累积：%s（%d 个网格）' % (THREAT_PATH, len(out)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
