#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
diagnose_live.py —— 对局后一键诊断：为什么实时预瞄还没出数据。

用法：
  python diagnose_live.py                 # 自动找最新一局回放
  python diagnose_live.py --replay X      # 指定回放
  python diagnose_live.py --at 240        # 同时看第 240 秒的时刻

它会逐项回答：
  1. 回放解出来了吗（header / 名单）
  2. 0x0a 里有多少敌方实体、位置和速度是否合理
  3. 可见性判定是否工作
  4. **0x2c 里的自舰位置能不能解出来**（当前唯一硬缺口）
  5. 面板该显示什么

对局进行中想看实时状态，加 --watch（会持续刷）。
"""
import argparse
import glob
import math
import os
import struct
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import live_aim as LA          # noqa: E402
from coord_scale import to_meters, K_METERS  # noqa: E402

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

OK, BAD, WARN = '[√]', '[×]', '[!]'


def latest_replay():
    d = os.path.join(os.environ.get('WOWS_PATH', r'J:\Games\World_of_Warships_CN360'), 'replays')
    files = [f for f in glob.glob(os.path.join(d, '*.wowsreplay')) if 'temp' not in f]
    if not files:
        return None
    files.sort(key=os.path.getmtime, reverse=True)
    return files[0]


def load(path):
    la = LA.LiveAim()
    t0 = time.time()
    with open(path, 'rb') as f:
        while True:
            c = f.read(65536)
            if not c:
                break
            la.feed(c)
    la.ensure_ident()
    return la, time.time() - t0


def report(la, elapsed, at=None):
    print('=' * 68)
    print('实时预瞄诊断报告')
    print('=' * 68)
    print(f'解密耗时 {elapsed:.1f} 秒，回放时长 {la.clock:.0f} 秒')
    print(f'坐标标定 k = {K_METERS} 米/单位')
    print()

    # --- 1. 基本解析 ---
    ident = la.ident
    mine = {e for e, (_, r) in ident.items() if r in (0, 1)}
    print(f'1. 名单：{len(ident)} 个有身份，{len(mine)} 个我方，me_id={la.me_id}')
    if not ident:
        print(f'   {BAD} 名单完全没解出来 —— EntityMethod 的 pickle 没抓到')
        return
    names = [n for (n, r) in ident.values()][:4]
    print(f'   样例：{names}')
    print()

    # --- 2. 敌方实体 ---
    pos_all = la.pos
    enem = la.enemy_ids()
    print(f'2. 位置流(0x0a)：共 {len(pos_all)} 个实体，其中非我方 {len(enem)} 个')
    if not pos_all:
        print(f'   {BAD} 没有任何 0x0a —— 包 type id 可能变了，或解密失败')
        return
    if not enem:
        print(f'   {WARN} 没识别出敌方 —— 检查 pos_off 推断（当前 {la._pos_off}）')
        print(f'         我方 avatarId: {sorted(mine)[:6]}')
        print(f'         0x0a 的键:      {sorted(pos_all)[:6]}')
        return
    print(f'   {OK} 敌方识别正常')
    print()

    # --- 3. 速度 ---
    print('3. 速度（长窗口首尾差分）')
    T = at if at is not None else la.clock
    rows = []
    for e in enem:
        v, a, mv = la._speed_vec(e, T)
        if a is None:
            continue
        sp = math.hypot(v[0], v[1]) * 1.94384 if v else 0.0
        rows.append((e, sp, a, T - a[0]))
    if not rows:
        print(f'   {BAD} t={T:.0f}s 时刻没有任何敌方有位置数据')
    else:
        moving = [r for r in rows if r[1] > 1]
        print(f'   t={T:.0f}s：有数据的 {len(rows)} 个，其中在移动的 {len(moving)} 个')
        for e, sp, a, stale in sorted(rows, key=lambda r: -r[1])[:8]:
            flag = '移动' if sp > 1 else '静止'
            print(f'     eid={e:<10} {sp:5.1f} kn  {flag}  位置({to_meters(a[1]):7.0f},'
                  f'{to_meters(a[2]):7.0f})m  数据陈旧 {stale:.0f}s')
        if moving:
            hi = max(r[1] for r in moving)
            print(f'   {OK if hi < 45 else BAD} 最高速度 {hi:.1f} kn'
                  f'（舰船上��约 40 kn）{"—— 合理" if hi < 45 else "—— 偏大，坐标标定可能有偏差"}')
    print()

    # --- 4. 可见性 ---
    fresh = [e for e, _, a, st in rows if st <= LA.VISIBLE_WINDOW]
    print(f'4. 可见性（最近 {LA.VISIBLE_WINDOW:.0f}s 内有 0x0a 更新 = 可见）')
    print(f'   当前可见的敌方：{len(fresh)} 个')
    print(f'   {OK} 判定通路正常（0x0a 更新即代表客户端能看到）')
    print()

    # --- 5. 自舰位置（硬缺口）---
    print('5. ★ 自舰位置（当前唯一硬缺口）')
    me = la.me_state(T)
    if me and me.get('pos'):
        print(f'   {OK} 解出自舰位置 {me["pos"]}')
    else:
        print(f'   {BAD} 拿不到自舰位置 → 距离/提前量无法计算')
        print('   原因：回放没有自舰的 Position 流（客户端本地知道自己的位置，')
        print('        不需要网络同步）；自舰坐标只在 0x2c 里，而该包给的是')
        print('        **不带 vehicleID 的紧凑位域列表**，无法确定哪个下标是我。')
    obs = la.last_observer
    print(f'   参考：0x2c 的 observer={obs}，me_id={la.me_id}，'
          f'{"一致 ✓" if obs and obs == la.me_id else "不一致 ✗"}')
    print(f'   0x2c 车队规模={la.vis_seq}，其中可见={la.vis_seen}，'
          f'下标={la.vis_idx[:8]}')
    print()

    # --- 6. 面板预期 ---
    snap = la.snapshot()
    print('6. 面板当前会显示：')
    print(f'   status = {snap.get("status")}，targets = {len(snap.get("targets") or [])}')
    if not snap.get('me'):
        print('   → 因自舰坐标缺失，距离/提前量列会是空的，')
        print('     这是预期行为（宁可空着也不给假数据）。')
    print()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--replay')
    ap.add_argument('--at', type=float)
    a = ap.parse_args()
    path = a.replay or latest_replay()
    if not path or not os.path.exists(path):
        print('[×] 找不到回放文件')
        return 2
    print(f'回放：{os.path.basename(path)}  ({os.path.getsize(path)/1024:.0f} KB)')
    print()
    la, el = load(path)
    report(la, el, a.at)
    return 0


if __name__ == '__main__':
    sys.exit(main())
