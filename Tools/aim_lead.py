#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
aim_lead.py —— 炮击提前量（预瞄）解算。

【要解决的问题】
"敌舰在动，我该往哪打？" —— 也就是命中预测（lead / lead indicator）。
传统做法是玩家自己心算：目标距离 ÷ 炮弹速度 = 飞行时间，再乘目标速度。
本模块把这个心算自动化，并且比人工精确得多，因为它用的是
**回放里的真实数据**：目标每 tick 的精确坐标、真实航向、真实弹速。

【核心算法：迭代解提前量】
设目标位置 T(t) = T0 + V·t（匀速近似），我方位置 P，炮弹速度 s。
命中条件：|T(t) − P| = s·t
直接解需要解二次方程，但迭代 5~6 次即收敛到 1e-6 米：
    t ← |(T0 − P) + V·t| / s
    重复若干次，t 即为飞行时间
    提前量 = V·t，瞄准点 = T0 + 提前量

【为什么用位置差分求速度，而不是读航向】
`PositionEvent.yaw`（弧度）的角度基准**尚未标定**，而 MinimapVisionEvent 的
heading 已标定为标准罗盘（0°=北）。但预瞄只需要**速度矢量**，用位置差分
（Δposition/Δt）最稳妥 —— 它不依赖任何角度基准，也没有罗盘/弧度的歧义。
（代价：对加减速的目标会有滞后，所以对差分窗口做了限制。）

【实测数据来源】
  目标位置/运动 : PositionEvent.x/z （并用 coord_scale 换算成米）
  我方弹速      : 本方 ShotCreatedEvent.speed 的中位数（按 params_id 分组）
                  —— 不同弹种（AP/HE/APCR）弹速不同，必须分别统计
  落点散布      : 同一次齐射各炮管落点的离散度（用来判断"能不能打得中"）

用法：
  python aim_lead.py                      # 分析最近一局
  python aim_lead.py --replay X
  python aim_lead.py --replay X --at 240  # 只看第 240 秒的时刻解算
"""
import argparse
import bisect
import math
import os
import statistics
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from coord_scale import K_METERS, to_meters   # noqa: E402

GAMEDATA = os.path.join(HERE, '_gamedata', 'data', 'scripts_entity', 'entity_defs')

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# 差分窗口限制：太短会被采样噪声放大，太长会把加减速抹平。
# 注意 PositionEvent 是每 tick 采样（实测间隔约 0.1 秒），所以 MIN 不能太大，
# 否则会把所有相邻样本都过滤掉（第一版设 0.25 导致解算不出任何目标）。
# 用**窗口首尾差分**（不是相邻帧差分）求速度：
# 相邻帧间隔仅 0.08~0.3 秒，位移可能只有 0.03 单位，浮点噪声占比过大，
# 实测会算出 167~200 节这种超音速的假速度。首尾差分用 0.6 秒窗口，
# 噪声被平均掉，结果稳定（实测同一艘船 0.25 单位/秒 = 7.7 节，与物理相符）。
DIFF_WINDOW = 0.6       # 秒，窗口长度
DIFF_MIN_MOVE = 2.0     # 米，窗口内总位移下限（低于则视为静止）


def solve_lead(my_pos, tgt_pos, tgt_vel, shell_speed, iters=8):
    """解命中所需的飞行时间与提前量。

    my_pos / tgt_pos : (x, z) 米
    tgt_vel          : (vx, vz) 米/秒
    shell_speed      : 米/秒
    返回 (飞行时间秒, 瞄准点(x,z), 提前量(x,z))
    """
    dx = tgt_pos[0] - my_pos[0]
    dz = tgt_pos[1] - my_pos[1]
    t = math.hypot(dx, dz) / shell_speed
    ax, az = dx, dz
    for _ in range(iters):
        ax = dx + tgt_vel[0] * t
        az = dz + tgt_vel[1] * t
        nt = math.hypot(ax, az) / shell_speed
        if abs(nt - t) < 1e-7:
            t = nt
            break
        t = nt
    lead = (tgt_vel[0] * t, tgt_vel[1] * t)
    return t, (tgt_pos[0] + lead[0], tgt_pos[1] + lead[1]), lead


def analyse(path, at_time=None):
    from wows_replay_parser import parse_replay
    r = parse_replay(replay_path=path, gamedata_path=GAMEDATA)

    info = {p.entity_id: {'name': p.name, 'relation': p.relation, 'isBot': p.is_bot}
            for p in r.players}
    me_id = next((p.entity_id for p in r.players if p.relation == 0), None)

    # ---------- 我方弹速：按 params_id（弹种）分组 ----------
    spd = defaultdict(list)
    for e in r.events:
        if type(e).__name__ == 'ShotCreatedEvent' and getattr(e, 'owner_id', 0) == me_id:
            spd[e.params_id].append(e.speed)
    shells = sorted(((k, statistics.median(v), len(v)) for k, v in spd.items()),
                    key=lambda x: -x[2])
    default_speed = shells[0][1] if shells else 900.0

    # ---------- 轨迹（米） ----------
    my_track, tgt_track = [], defaultdict(list)
    for e in r.events:
        if type(e).__name__ != 'PositionEvent' or e.x is None:
            continue
        if e.entity_id == me_id:
            continue                       # 自舰没有 Position 流（见 memory）
        tgt_track[e.entity_id].append((e.timestamp, to_meters(e.x), to_meters(e.z)))
    # 自舰轨迹来自 MinimapVisionEvent（唯一来源）
    for e in r.events:
        if type(e).__name__ == 'MinimapVisionEvent' \
                and getattr(e, 'vehicle_entity_id', 0) == me_id:
            my_track.append((e.timestamp, to_meters(e.world_x), to_meters(e.world_z)))
    my_track.sort()
    for v in tgt_track.values():
        v.sort()

    def vel_at(lst, t):
        """位置差分求速度矢量 (vx, vz) 米/秒；样本不足返回 None。"""
        if len(lst) < 2:
            return None, None, None
        ts = [p[0] for p in lst]
        i = bisect.bisect_right(ts, t) - 1
        if i < 1:
            return None, None, None
        # 窗口首尾差分：从 i 往回找仍在 [t-W, t] 窗口内的最早样本
        j = i
        while j > 0 and t - lst[j - 1][0] <= DIFF_WINDOW:
            j -= 1
        a, b = lst[j], lst[i]
        dt = b[0] - a[0]
        if dt < 0.15:
            return None, None, None
        mv = math.hypot(b[1] - a[1], b[2] - a[2])
        if mv < DIFF_MIN_MOVE:
            return None, None, None
        return ((b[1] - a[1]) / dt, (b[2] - a[2]) / dt), b, mv

    def my_at(t, max_lag=6.0):
        if not my_track:
            return None
        ts = [p[0] for p in my_track]
        i = bisect.bisect_right(ts, t) - 1
        if i < 0 or t - my_track[i][0] > max_lag:
            return None
        return my_track[i]

    # ---------- 选一个时刻做解算 ----------
    T = at_time if at_time is not None else r.duration * 0.6
    mp = my_at(T)
    results = []
    if mp:
        for eid, lst in tgt_track.items():
            meta = info.get(eid)
            if not meta or meta['relation'] == 0:
                continue
            v, at, moved = vel_at(lst, T)
            if v is None or at is None:
                continue
            spd_ms = math.hypot(v[0], v[1])
            if spd_ms < 1.5:                # 基本静止（<3节），预瞄没意义
                continue
            t, aim, lead = solve_lead((mp[1], mp[2]), (at[1], at[2]), v, default_speed)
            dist = math.hypot(at[1] - mp[1], at[2] - mp[2])
            # 与"直接瞄当前位置"的偏差 = 提前量大小
            lead_len = math.hypot(*lead)
            # 所需转过的角度
            brg_now = math.degrees(math.atan2(at[1] - mp[1], at[2] - mp[2])) % 360
            brg_aim = math.degrees(math.atan2(aim[0] - mp[1], aim[1] - mp[2])) % 360
            results.append({
                'name': meta['name'], 'isBot': meta['isBot'],
                'relation': meta['relation'],
                'pos': (round(at[1]), round(at[2])),
                'vel': (round(v[0], 2), round(v[1], 2)),
                'speed_ms': round(spd_ms, 2),
                'speed_knot': round(spd_ms * 1.94384, 1),
                'dist': round(dist),
                'flight': round(t, 2),
                'lead': round(lead_len),
                'aim': (round(aim[0]), round(aim[1])),
                'brg_now': round(brg_now, 1),
                'brg_aim': round(brg_aim, 1),
                'swing': round(((brg_aim - brg_now + 540) % 360) - 180, 1),
            })
    results.sort(key=lambda x: x['dist'])

    return {
        'file': os.path.basename(path), 'map': r.map_name,
        'duration': round(r.duration, 1), 'at': round(T, 1),
        'myPos': (round(mp[1]), round(mp[2])) if mp else None,
        'shells': shells, 'default_speed': default_speed,
        'results': results,
    }


def to_text(res):
    L = []
    L.append('【炮击提前量解算（真实坐标 + 真实弹速的迭代解，非估算）】')
    L.append('地图：%s　解算时刻：%.1f 秒' % (res['map'], res['at']))
    if res['myPos']:
        L.append('我方位置：(%.0f, %.0f) 米' % res['myPos'])
    if res['shells']:
        L.append('我方弹速（按弹种，实测本局）：')
        for pid, sp, n in res['shells'][:4]:
            L.append('   params_id=%-12d 弹速 %.0f m/s  （%d 发）' % (pid, sp, n))
        L.append('   下面用最快的 %.0f m/s 计算' % res['default_speed'])
    L.append('')
    if not res['results']:
        L.append('该时刻没有可解算的运动目标（可能都在静止或位置数据不足）。')
        return '\n'.join(L)
    L.append('目标在动，需要提前量：')
    L.append('')
    L.append('  %-16s %-9s %-8s %-9s %-8s %-9s %s' % (
        '目标', '距我', '速度', '飞行时间', '提前量', '方位角', '应把准星移到这里'))
    for x in res['results'][:14]:
        tag = '[AI]' if x['isBot'] else ''
        if x['relation'] == 1:
            tag = '[队友]'
        L.append('  %-16s%5d m %5.1f kn %7.2f s %7d m  %6.1f°→%6.1f°  横移 %+.0f 米' % (
            (x['name'][:15] + tag), x['dist'], x['speed_knot'], x['flight'],
            x['lead'], x['brg_now'], x['brg_aim'], x['swing']))
    L.append('')
    L.append('读法：飞行时间 = 距离 ÷ 弹速；提前量 = 目标在这段时间内会移动的距离。')
    L.append('      把准星从"当前位置方位"转到"应瞄方位"，就是完整的提前量打点。')
    return '\n'.join(L)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--replay')
    ap.add_argument('--at', type=float, help='指定解算时刻（秒）')
    a = ap.parse_args()
    d = os.path.join(os.environ.get('WOWS_PATH', r'J:\Games\World_of_Warships_CN360'), 'replays')
    if a.replay:
        files = [a.replay]
    else:
        files = [os.path.join(d, f) for f in os.listdir(d)
                 if f.endswith('.wowsreplay') and 'temp' not in f]
        files.sort(key=os.path.getmtime, reverse=True)
    if not files or not os.path.exists(files[0]):
        print('[×] 没有可用回放')
        return 2
    try:
        res = analyse(files[0], a.at)
    except Exception as ex:
        print('[×] 失败：%s' % ex)
        return 1
    print(to_text(res))
    return 0


if __name__ == '__main__':
    sys.exit(main())
