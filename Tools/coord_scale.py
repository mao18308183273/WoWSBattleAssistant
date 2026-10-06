#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
coord_scale.py —— 回放坐标单位标定（这是整个项目最关键的常量）。

【问题】
`.wowsreplay` 里的坐标**不是米**。实测多局坐标跨度只有 600~1250，而 WoWS
地图是 15×15 公里。早期直接拿坐标距离当米用，导致：
  - 误判"敌人在我 300 米处"（实际 4800 米）
  - 飞行时间算成 0.15~0.5 秒（实际 8 秒）
  - 结论完全错误

【标定方法：利用 speed 字段 + 长窗口差分交叉验证】
`PositionEvent.speed` 的单位是 **km/h**（停船为 0，量级与舰船 0~40 节吻合）。
于是：
    真实速度(m/s) = speed / 3.6
    表观速度(单位/秒) = 坐标位移 / 时间
    k = 真实速度 / 表观速度       ← 缩放因子

★ 关键：**必须用 0.4~0.6 秒长窗口的首尾差分，不能用相邻帧差分。**
Position 相邻帧间隔仅 0.08~0.3 秒、位移常常只有 0.03~0.09 坐标单位，
在这种窗口下坐标量化误差被放大，实测会算出 167~200 节的速度，
并让 k 被低估/高估近一倍（第一版误得 k=15.9，第二版用相邻帧又得 16.4）。
改用 0.5 秒窗口后，5 艘船独立给出 k = 7.43~9.71，中位数 **8.15**。

【两个独立交叉验证都支持 k≈8.15】
1. 物理上限：某驱逐舰差分 2.488 单位/秒 × 8.15 = 20.3 m/s = **39.4 节**
   —— 正好是 WoWS 驱逐舰满速上限；若取 16.4 则算出 79 节，物理不可能。
2. 地图尺寸：37_Ridge 坐标跨度 1251（视为半轴）× 2 × 8.15 ≈ **20.4 km**，
   符合 WoWS 15~20 km 级地图；若取 16.4 则为 41 km，明显过大。

【为什么仍保留 calibrate()】
离散度约 ±15%（speed 字段是整数、低速时相对误差大，加速/减速时
瞬时值不稳）。战术判断用常量即可；复盘/教学想要更高精度可现场统计。
"""
import math
import os
import statistics
import sys

# ★ 标定结果：1 坐标单位 = 8.15 米
#   （0.5 秒长窗口逐船核算，5 艘船 7.43~9.71；经"舰船速度上限"与
#     "地图尺寸"两个独立物理量交叉验证）
K_METERS = 8.15
K_MIN, K_MAX = 7.43, 9.71          # 实测范围，用于标注不确定度

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def to_meters(units, k=K_METERS):
    """坐标单位 → 米"""
    return units * k


def to_units(meters, k=K_METERS):
    """米 → 坐标单位"""
    return meters / k


def calibrate(replay, me_id=None, gamedata=None):
    """从一局回放现场统计 k。用于对精度要求高的场景（如复盘教学）。"""
    from wows_replay_parser import parse_replay
    if gamedata is None:
        gamedata = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '_gamedata', 'data', 'scripts_entity', 'entity_defs')
    r = parse_replay(replay_path=replay, gamedata_path=gamedata)
    if me_id is None:
        me_id = next((p.entity_id for p in r.players if p.relation == 0), None)

    tr = {}
    for e in r.events:
        if type(e).__name__ != 'PositionEvent' or e.entity_id == me_id:
            continue
        if e.speed is None or e.speed <= 1:
            continue
        l = tr.setdefault(e.entity_id, [])
        if not l or e.timestamp - l[-1][0] > 0.5:
            l.append((e.timestamp, e.x, e.z, e.speed))

    # ★ 长窗口（0.5 秒）首尾差分 —— 见模块文档的说明，窗口太短会算错近一倍
    WINDOW = 0.5
    ks = []
    for l in tr.values():
        for i in range(1, len(l)):
            if l[i][3] is None or l[i][3] < 50:   # 只取明确在高速移动的时段
                continue
            j = i
            while j > 0 and l[i][0] - l[j - 1][0] <= WINDOW:
                j -= 1
            dt = l[i][0] - l[j][0]
            if dt < 0.3:
                continue
            d = math.hypot(l[i][1] - l[j][1], l[i][2] - l[j][2])
            if d < 1.0:
                continue
            ks.append((l[i][3] / 3.6) / (d / dt))   # km/h -> m/s，再除以单位/秒
    if not ks:
        return None
    ks.sort()
    return {
        'n': len(ks),
        'k': statistics.median(ks),
        'p25': ks[len(ks) // 4],
        'p75': ks[3 * len(ks) // 4],
    }


if __name__ == '__main__':
    import glob
    d = os.path.join(os.environ.get('WOWS_PATH', r'J:\Games\World_of_Warships_CN360'), 'replays')
    files = [f for f in sorted(glob.glob(os.path.join(d, '*.wowsreplay')),
                               key=os.path.getmtime, reverse=True) if 'temp' not in f]
    if not files:
        print('找不到回放')
        sys.exit(1)
    print('坐标单位标定（1 单位 = ? 米）\n')
    allk = []
    for f in files[:5]:
        try:
            r = calibrate(f)
        except Exception as ex:
            print('  %-44s 失败 %s' % (os.path.basename(f)[:44], str(ex)[:50]))
            continue
        if r:
            allk.append(r['k'])
            print('  %-44s n=%5d  k=%.2f  [%.1f~%.1f]'
                  % (os.path.basename(f)[:44], r['n'], r['k'], r['p25'], r['p75']))
    if allk:
        k = statistics.median(allk)
        print('\n  全局 k = %.2f  →  1 坐标单位 = %.2f 米' % (k, k))
        print('  常用换算：100 单位=%.0f米  500=%.0f米  1000=%.0f米'
              % (100 * k, 500 * k, 1000 * k))
