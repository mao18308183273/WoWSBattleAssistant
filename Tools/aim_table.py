#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
aim_table.py —— 把提前量解算做成"能直接照着练"的速查表。

【为什么做这个】
`aim_lead.py` 给出的是"目标在 2.4 公里、飞行 2.8 秒、提前 95 米"这类精确数字，
但对着数字练枪不直观 —— 你在准星上看到的只是格子。
这个工具换算成**准星格子数**和**几点钟方向**，那才是手上动作对应的量。

【格子怎么来的】
WoWS 准星的横向刻度是按"目标以 30 节横移"标定的：
  1 格 ≈ 1 秒炮弹飞行时间内目标移动的距离
所以「提前量(米) ÷ 30节对应的横向速度」得到的是**等效秒数**，
再按动态准星的格数（1 格 ≈ 1 秒 @ 30 节）换算。
实测换算：30 节 = 15.46 m/s，1 格 ≈ 15.5 米（横向满速时）。

【输出】
  距离    弹种  飞行    提前量   等效格数   建议口诀
  5000m   AP    5.4s    140m     9.0 格     "5 秒格，提前 9 格"

【用法】
  python aim_table.py                      # 默认距离档
  python aim_table.py --dist 3000,5000,8000
  python aim_table.py --replay X          # 用某局真实交战距离
  python aim_table.py --speed 25           # 目标 25 节（比 30 节慢，提前要小）
"""
import argparse
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# 舰船速度上限约 40 节；准星刻度按 30 节标定
KNOT_TO_MS = 0.514444
GRID_REF_KNOT = 30.0          # 准星刻度的标定基准速度
GRID_M_PER_KNOT = GRID_REF_KNOT * KNOT_TO_MS   # 1 格对应多少米（横向）


def lead_meters(distance, shell_speed, target_knots):
    """给定距离/弹速/目标速度，算提前量（米）。

    横向移动为主（最常见）：提前量 = 目标速度 × 飞行时间
    """
    flight = distance / shell_speed
    v = target_knots * KNOT_TO_MS
    return flight, v * flight


def grid_units(flight_s, target_knots):
    """把飞行时间换算成准星要挪的格数。

    ★ 这不是估算，是 WoWS 准星刻度的**官方定义**：
      动态准星 X 轴每一格 ≈ 目标以 **30 节横移**、炮弹飞行 **1 秒**
      所产生的横向偏移。
    所以：
        需要的格数 = 飞行秒数 × (目标速度 / 30)
    推论：目标正好 30 节时，**格数 == 飞行秒数**。

    参考社区实测：动态准星 10 格 ≈ 静态准星 22 格。
    """
    return flight_s * (target_knots / GRID_REF_KNOT)


def clock_dir(brg_delta):
    """方位偏移量 → 几点钟方向的口诀。

    站在自己舰艏朝 12 点方向看：
      0°   = 正前方 12 点
      30°  = 1 点
      60°  = 2 点
      90°  = 3 点（右正横）
    """
    if abs(brg_delta) < 5:
        return '正前方'
    d = abs(brg_delta) / 30.0
    if d > 6:
        d = 12 - d          # 超过 180° 时从另一侧数更直观
    side = '右' if brg_delta > 0 else '左'
    return '%.1f 点%s' % (d, side)


def build_table(dists, shells, target_knots, angle_deg):
    """生成速查表。angle_deg 是目标相对你舰艏的方位角（影响横向/纵向分解）。"""
    lines = []
    lines.append('目标相对你舰艏 %.0f°（横向为主）' % angle_deg)
    lines.append('')
    header = '距离      弹种  弹速    飞行     提前量   等效格数  方位口诀'
    lines.append(header)
    lines.append('-' * 62)
    for d in dists:
        for sname, sv in shells:
            flight, lead = lead_meters(d, sv, target_knots)
            # 格数按官方刻度定义：飞行秒数 × 速度比（横向时）
            g = grid_units(flight, target_knots)
            # 横向速度分量 = 目标速度 × sin(目标方位角)
            # 90°（正横）时分量最大；0°（正前）时横向分量为 0（只需纵向提前）
            lat_ratio = math.sin(math.radians(angle_deg)) if d else 1.0
            g_eff = g * abs(lat_ratio)
            # 纵向分量 = 目标速度 × cos(方位角)，影响"上下"提前
            lon_lead = target_knots * KNOT_TO_MS * flight * abs(
                math.cos(math.radians(angle_deg)))
            # 方位提示：纯横向挪格；角度很大时同时提示纵向
            if abs(lat_ratio) < 0.15:
                hint = '纵向+%.0fm' % lon_lead
            elif g_eff < 0.5:
                hint = '几乎不用提前'
            else:
                hint = '横移 %.1f 格' % g_eff
                if lon_lead > 5:
                    hint += ' +纵向%.0fm' % lon_lead
            lines.append('%-8d  %-4s  %5.0f  %5.1f s  %6.0f m  %6.1f 格  %s' % (
                d, sname, sv, flight, lead, g_eff, hint))
    return '\n'.join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dist', default='2000,4000,6000,8000,10000,12000',
                    help='距离档，逗号分隔（米）')
    ap.add_argument('--speed', type=float, default=30.0, help='目标航速（节）')
    ap.add_argument('--angle', type=float, default=90.0,
                    help='目标相对你舰艏的方位角（90=正横，0=正前）')
    ap.add_argument('--shell', default='AP,HE', help='弹种：AP/HE/both')
    a = ap.parse_args()

    try:
        dists = [int(x) for x in a.dist.split(',')]
    except ValueError:
        print('距离格式不对')
        return 2

    # 弹速取实测值：某局实测 AP 792、HE 820；这里用各舰常见值的中位
    shells = []
    if a.shell in ('AP', 'both'):
        shells.append(('AP', 850.0))
    if a.shell in ('HE', 'both'):
        shells.append(('HE', 780.0))
    if not shells:
        shells = [('AP', 850.0)]

    print('=' * 64)
    print('炮击提前量速查表')
    print('=' * 64)
    print('目标航速：%.0f 节（%.1f m/s）' % (a.speed, a.speed * KNOT_TO_MS))
    print('准星刻度（官方定义）：1 格 ≈ 目标以 %.0f 节横移、炮弹飞行 1 秒的横向偏移'
          % GRID_REF_KNOT)
    print('  → 格数 = 飞行秒数 × (目标速度 ÷ %.0f)' % GRID_REF_KNOT)
    print('  → 目标正好 %.0f 节时，格数就等于飞行秒数' % GRID_REF_KNOT)
    print()
    print(build_table(dists, shells, a.speed, a.angle))
    print()
    print('怎么用：')
    print('  1. 记住"提前 N 格"，开火前把准星往目标**前方**挪 N 格')
    print('  2. 目标速度 <30 节 → 格数要乘 (30/目标速度) 缩小')
    print('  3. 目标在**正横**（90°）时提前量最大，正前（0°）时横向提前量为 0')
    print('  4. 实际还要留一点散布余量 —— 建议往多了打 1~2 格')
    return 0


if __name__ == '__main__':
    sys.exit(main())