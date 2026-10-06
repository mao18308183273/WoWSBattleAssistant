#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
live_aim.py —— **对局进行中**的炮击提前量（预瞄）解算器。

【定位】
`aim_lead.py` 是事后复盘（整局回放，慢但准）。
本模块是实时版：持续增量读取 `replays\temp.wowsreplay`（游戏正在写的那个文件），
每 0.5 秒解算一次当前所有目标的提前量，写成 JSON 供 WPF 端读取。

【数据来源（全部是服务端权威数据，不做任何视觉推测）】
  自舰位置 : MinimapVisionEvent(type 0x2c) 的 world_x/world_z
             —— 回放里**没有自舰的 Position 流**（客户端本地知道自己的位置，
                不需要网络同步），所以这是自舰位置的唯一来源
  自舰航向 : 由连续两次自舰位置差分求方向（不依赖任何角度基准，
             因为 PositionEvent.yaw 的基准至今未标定）
  敌方位置 : PositionEvent(type 0x0a) 的 x/z
  敌方速度 : PositionEvent 的窗口首尾差分（★必须用长窗口，见 coord_scale.py）
  敌方身份 : EntityMethod 里的 players 名单 + header.vehicles
  **点亮状态**: MinimapVisionEvent 出现 = 该实体当前可见；不出现 = 不可见。
             这是游戏本身的可见性判定，比我们自己猜准得多。

【性能】
纯 Python Blowfish 解密约 5 KB/0.5 秒。对局中 temp.wowsreplay 每分钟才增长
几百 KB，所以增量解密完全跟得上——这也是必须做流式、不能整文件重解的原因。

【输出 JSON】
{
  "updated": "...", "clock": 256.2, "status": "recording",
  "k_meters": 8.15,
  "me": {"pos_m": [x, z], "heading_deg": 194.1, "speed_kn": 22.4},
  "shell_speed": 900.0,
  "targets": [
     {"name":":Tegetthoff:", "eid":1158200, "visible": true,  "dist": 2363,
      "speed_kn": 18.4, "flight": 2.63, "lead": 25, "brg_now": 308.4,
      "brg_aim": 309.0, "swing": 0.6, "hit_prob": 0.62, "moving": true}
  ]
}
`hit_prob` 是把散布半径算进去后的命中概率估计（见 aim_lead 的散布模型）。

用法：
  python live_aim.py --watch "J:\\Games\\...\\replays" --out live_aim.json
  python live_aim.py --replay xxx.wowsreplay     # 离线自检
"""
import argparse
import glob
import json
import math
import os
import struct
import sys
import time
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import replay_live as rl                       # 复用流式解密
from coord_scale import K_METERS, to_meters    # 坐标单位 → 米
from aim_lead import solve_lead                # 提前量迭代解

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

PACKET_POSITION = 0x0a       # PositionEvent：敌方位置
PACKET_VISION = 0x2c         # MinimapVisionEvent：可见性 + 自舰位置

DIFF_WINDOW = 0.5            # 速度差分窗口（秒）—— 必须长窗口，见 coord_scale.py
MIN_MOVE = 2.0               # 窗口内最小位移（米），低于视为静止
DEFAULT_SHELL = 900.0        # 缺省弹速 m/s（实测范围 550~1050）
# 散布半径模型（WoWS 经验值，随距离增长）：sigma = a + b * dist_km
SPREAD_A, SPREAD_B = 6.0, 22.0


def bearing_deg(a, b):
    """从 a 指向 b 的罗盘方位角（0=北/+z，90=东/+x）。"""
    return math.degrees(math.atan2(b[0] - a[0], b[1] - a[1])) % 360


def ang_diff(a, b):
    return ((a - b + 540) % 360) - 180


class LiveAim:
    def __init__(self):
        self.stream = rl.ReplayStream()
        # eid -> (t, x, z)  坐标单位，来自 0x2c。**同时用两个 id 建索引**：
        #   body[0:4] = entity_id（可见性发起方，常是观察者）
        #   body[4:8] = vehicle_entity_id（被点亮的那个实体）
        # 实测"我自己"要靠 vehicle_entity_id 才能匹配上（wows-replay-parser
        # 也是用这个字段取到 766 条自舰样本的）。
        self.vis = {}            # eid -> 首次出现 (t,x,z)
        self.vis_last = {}       # eid -> 最近一次 (t,x,z)
        self.vis_time = {}       # eid -> 首次出现时刻
        self.vis_by_vehicle = {}
        self.pos = defaultdict(list)   # eid -> [(t, x, z)] 坐标单位，来自 0x0a
        self.ident = {}          # eid -> 名字
        self.clock = 0.0
        self.me_id = None         # 自舰 avatarId（由 EntityMethod 名单里 relation==0 确定）
        self.my_hist = []         # 自舰历史位置（用于求航向）
        self._vis_off = None      # 0x2c eid → avatarId 的偏移（以自舰为锚点推断）
        self._pos_off = None      # 0x0a eid → avatarId 的偏移
        self._by_entity = {}      # avatarId → {entityId,...}

    # ---------------------------------------------------------- 增量喂数据
    def feed(self, chunk):
        before = len(self.stream.packets)
        self.stream.feed(chunk)
        for ptype, pt, body in self.stream.packets[before:]:
            self._on_packet(ptype, pt, body)

    def _on_packet(self, ptype, pt, body):
        if pt > self.clock:
            self.clock = pt
        if ptype == PACKET_VISION and len(body) >= 24:
            # ★ 字段偏移经过实测校准（之前错位 12 字节，导致绝大多数包被丢弃）：
            #   +0  entity_id        (i4)
            #   +4  vehicle_entity_id(i4)  本局实测恒为 0，不可依赖
            #   +8  world_x          (f4)
            #   +16 world_z          (f4)
            #   +20 heading          (f4)  弧度
            eid = struct.unpack_from('<i', body, 0)[0]
            vid = struct.unpack_from('<i', body, 4)[0]
            x = struct.unpack_from('<f', body, 8)[0]
            z = struct.unpack_from('<f', body, 16)[0]
            hdg = struct.unpack_from('<f', body, 20)[0]
            if eid and (x or z):
                rec = (pt, x, z)
                self.vis_last[eid] = rec
                self.vis_time.setdefault(eid, pt)
                if vid:
                    self.vis_last[vid] = rec
                    self.vis_time.setdefault(vid, pt)
        elif ptype == PACKET_POSITION and len(body) >= 33:
            eid = struct.unpack_from('<i', body, 0)[0]
            if not eid:
                return
            l = self.pos[eid]
            if not l or pt - l[-1][0] > 0.3:
                l.append((pt, struct.unpack_from('<f', body, 8)[0],
                          struct.unpack_from('<f', body, 12)[0]))

    # ---------------------------------------------------------- 身份识别
    def ensure_ident(self):
        """建立 id → 身份 的映射。

        ★ **必须用 EntityMethod 的 pickle（build_links），不能用 header.vehicles**。
        三套 id 各管一段，实测对不上：
            header.vehicles[].id      = 537082475（玩家/实体 id 空间）
            MinimapVisionEvent.eid   = 1272837  （avatarId 空间）
            PositionEvent.eid        = 又是另一个（= avatarId + 偏移）
        build_links 输出的 by_avatar 正是 avatarId → 身份，所以 vis/pos 都能对上。
        """
        if self.ident or not self.stream.packets:
            return
        try:
            by_avatar, by_entity = rl.build_links(self.stream)
        except Exception:
            return
        if not by_avatar and not ((self.stream.header or {}).get('vehicles') or []):
            return
        for av, info in by_avatar.items():
            rel = info.get('relation')
            if rel is None:
                # build_links 里 relation 可能缺失，用 header.vehicles 补
                rel = self._rel_from_header(info.get('entityId'))
            self.ident[av] = (info.get('rawName') or '?', rel)
            if rel == 0 and self.me_id is None:
                self.me_id = av
        self._by_entity = by_entity
        # build_links 只能从 pickle 解出一部分玩家（实测 7/17），
        # 用 header.vehicles 通过 entityId 补齐剩余的
        for v in ((self.stream.header or {}).get('vehicles') or []):
            if not isinstance(v, dict) or v.get('id') is None:
                continue
            ei = int(v['id'])
            hit = next((av for av, inf in by_avatar.items()
                        if inf.get('entityId') == ei), None)
            if hit is not None:
                continue
            # 用 entityId 反推一个占位 avatarId（真实 avatarId 需从 pickle 拿，
            # 拿不到时靠偏移推断也够用）
            if ei not in self.ident:
                self.ident[ei] = (v.get('name') or '?', v.get('relation'))
        # relation 兜底：若 header 能对上 entityId 就用 header 的
        hv = {}
        for v in ((self.stream.header or {}).get('vehicles') or []):
            if isinstance(v, dict) and v.get('id') is not None:
                hv[int(v['id'])] = v.get('relation')
        for av, info in list(self.ident.items()):
            if info[1] is None:
                r = hv.get(info.get('entityId') if False else None)
        # 把 entityId→relation 缓存下来用
        self._rel_cache = hv
        for av, info in list(self.ident.items()):
            if info[1] is None and self._by_entity:
                ei = self._by_entity.get(av, {}).get('entityId')
                if ei is not None and ei in hv:
                    self.ident[av] = (info[0], hv[ei])
                    if hv[ei] == 0 and self.me_id is None:
                        self.me_id = av

    def infer_offsets(self):
        """推断包里的 eid 与 avatarId 之间的固定偏移。

        ★ **以自舰为锚点**，不要用"集合重合数"去猜：
        build_links 只能从 pickle 解出部分玩家（实测 7/17），ident 不完整时
        统计重合会给出错误答案（实测 ident 只有 7 条时会误推出 -3/+3，
        而真值是 +1）。自舰必然出现在 MinimapVisionEvent 里（它也需要被
        报告可见性），所以用"哪个偏移能让 me_id 命中 vis"来定，可靠得多。
        """
        if self._vis_off is not None:
            return
        self._vis_off = 0
        if self.me_id is not None and self.vis_last:
            for off in range(-4, 5):
                if (self.me_id + off) in self.vis_last:
                    self._vis_off = off
                    break
        # Position 的偏移同理：先用自舰（若有），否则退回统计
        self._pos_off = None
        if self.me_id is not None and self.me_id in self.pos:
            self._pos_off = 0
        elif self.ident and self.pos:
            ids = set(self.ident)
            best, hit = 0, -1
            for off in range(-4, 5):
                c = sum(1 for e in self.pos if (e - off) in ids)
                if c > hit:
                    best, hit = off, c
            self._pos_off = best if hit > 0 else 0
        else:
            self._pos_off = 0

    def vid_of(self, eid):
        """把包里的 eid 归一到 avatarId 空间。"""
        return eid - (self._vis_off or 0)

    def _guess_name(self, vid):
        """给 AI 实体找名字：AI 没有 avatarId，但其 entityId 在 EntityMethod
        的 pickle 里出现过（wows-replay-parser 就是这么解出 :Borckenhagen: 的）。
        这里做一次宽松扫描，代价可接受（只在首次解算时跑）。"""
        if getattr(self, '_names_done', False):
            return (self._extra_names or {}).get(vid)
        self._names_done = True
        self._extra_names = {}
        try:
            from replay_spawn_scan import _try_unpickle, _iter_ship_records
            for ptype, pt, body in self.stream.packets:
                if ptype != 0x08 or len(body) < 200:
                    continue
                for obj in _try_unpickle(body):
                    for rec in _iter_ship_records(obj):
                        # rec = [(key, value), ...]
                        d = dict(rec)
                        nm = d.get('name')
                        ei = d.get('id')
                        if isinstance(nm, bytes):
                            nm = nm.decode('utf-8', 'replace')
                        if nm and isinstance(ei, int) and isinstance(nm, str):
                            self._extra_names.setdefault(int(ei), nm)
        except Exception:
            pass
        # entityId -> avatarId 无法直连时，用位置流的 eid 反查
        return None

    def _rel_from_header(self, entity_id):
        if entity_id is None:
            return None
        if not hasattr(self, '_rel_cache') or self._rel_cache is None:
            self._rel_cache = {}
            for v in ((self.stream.header or {}).get('vehicles') or []):
                if isinstance(v, dict) and v.get('id') is not None:
                    self._rel_cache[int(v['id'])] = v.get('relation')
        return self._rel_cache.get(int(entity_id))

    def enemy_ids(self):
        """发现敌方实体。

        ★ **不能只靠 header.vehicles**：PVE 行动模式（如 s02_Naval_Defense）
        的 header.vehicles 里只有 7 名真人玩家，敌方 AI 是引擎刷出来的，
        根本不在名单里 —— 实测因此识别出 0 个敌人。
        ★ 也不可靠 build_links：AI 实体只有 entityId、**没有 avatarId**
        （实测 AI 的 avatarId=0），所以 by_avatar 里不含 AI。

        可靠做法：同时具备「有 Position 轨迹」且「有 MinimapVision 记录」的
        非我方实体 = 敌方舰船。vision 记录同时过滤掉了鱼雷/飞机/水雷等
        非舰船实体（它们不会出现在可见性列表里）。
        """
        known = {e for e, (_, rel) in self.ident.items() if rel in (0, 1)}
        vis_ids = set(self.vis_last) | set(self.vis_by_vehicle)
        out = []
        for eid in self.pos:
            vid = eid - (self._pos_off or 0)
            if vid in known or eid in known:
                continue
            # 必须有可见性记录才认定为舰船
            if any(self.vid_of(x) == vid for x in vis_ids):
                if vid not in out:
                    out.append(vid)
        return out

    # ---------------------------------------------------------- 解算
    def _speed_vec(self, eid, t):
        """长窗口首尾差分求速度（米/秒）。★窗口短会算出 167~200 节的假速度。"""
        l = self.pos.get(eid)
        if not l or len(l) < 3:
            return None, None, 0.0
        # 找 t 之前最后一个样本 i
        i = len(l) - 1
        while i > 0 and l[i][0] > t:
            i -= 1
        if i < 1:
            return None, None, 0.0
        j = i
        while j > 0 and l[i][0] - l[j - 1][0] <= DIFF_WINDOW:
            j -= 1
        a, b = l[j], l[i]
        dt = b[0] - a[0]
        if dt < 0.2:
            return None, None, 0.0
        mv = to_meters(math.hypot(b[1] - a[1], b[2] - a[2]))
        if mv < MIN_MOVE:
            return None, None, 0.0
        return ((to_meters(b[1] - a[1]) / dt, to_meters(b[2] - a[2]) / dt),
                b, mv)

    def me_state(self, t):
        """自舰位置与航向。

        ★ 自舰位置的**唯一来源**是 MinimapVisionEvent(0x2c) 且 entity_id ==
        header.vehicles 里 relation==0 的那条 —— 回放里没有自舰的 Position 流
        （客户端本地知道自己的位置，不需要网络同步）。
        航向用自舰自身相邻两次 0x2c 位置差分求得，不依赖任何角度基准。
        """
        self.infer_offsets()
        cand = {e: v for e, v in self.vis_last.items()
                if self.vid_of(e) == self.me_id}
        if not cand:
            cand = {e: v for e, v in self.vis_by_vehicle.items()
                    if self.vid_of(e) == self.me_id}
        if not cand:
            return None
        if not cand:
            return None
        rec = max(cand.values(), key=lambda r: r[0])
        pt, x, z = rec
        p = (to_meters(x), to_meters(z))
        # 历史序列：只在样本间隔合理时追加
        if not self.my_hist or pt - self.my_hist[-1][0] > 0.2:
            self.my_hist.append((pt, x, z))
            if len(self.my_hist) > 40:
                self.my_hist.pop(0)
        hdg, spd = None, 0.0
        if len(self.my_hist) >= 2:
            # 找 0.5 秒前的那一点做长窗口差分（短窗口会算出假速度）
            j = len(self.my_hist) - 1
            while j > 0 and pt - self.my_hist[j - 1][0] <= 0.5:
                j -= 1
            a = self.my_hist[j]
            dt = a[0] - self.my_hist[0][0]
            mv = math.hypot(a[1] - self.my_hist[0][1], a[2] - self.my_hist[0][2])
            if dt > 0.15 and mv > 0.5:
                hdg = bearing_deg((to_meters(self.my_hist[0][1]),
                                   to_meters(self.my_hist[0][2])),
                                  (to_meters(a[1]), to_meters(a[2])))
                spd = mv / dt * 1.94384
        return {'pos': p, 't': pt, 'heading': hdg, 'speed_kn': round(spd, 1)}

    def solve(self, shell_speed=DEFAULT_SHELL):
        t = self.clock
        me = self.me_state(t)
        if not me:
            return None
        my = me['pos']
        out = []
        self.infer_offsets()
        for eid in self.enemy_ids():
            v, at, moved = self._speed_vec(eid, t)
            if at is None:
                continue
            tp = (to_meters(at[1]), to_meters(at[2]))
            dist = math.hypot(tp[0] - my[0], tp[1] - my[1])
            if dist < 50:
                continue
            spd = 0.0
            if v:
                spd = math.hypot(v[0], v[1])
            if spd >= 0.5:
                flight, aim, lead = solve_lead(my, tp, v, shell_speed)
            else:
                d = math.hypot(tp[0] - my[0], tp[1] - my[1])
                flight, lead, aim = d / shell_speed, (0.0, 0.0), tp
            # 散布 → 命中概率
            sigma = SPREAD_A + SPREAD_B * (dist / 1000.0)
            hit_prob = max(0.0, min(1.0, 1.0 - sigma / max(60.0, dist)))
            nm, rel = self.ident.get(eid, (None, 2))
            if not nm:
                nm = self._guess_name(eid) or '敌方%d' % (len(out) + 1)
            brg_now = bearing_deg(my, tp)
            brg_aim = bearing_deg(my, aim)
            out.append({
                'eid': eid, 'name': nm, 'relation': rel,
                'visible': any(self.vid_of(x) == eid
                               for x in (set(self.vis_last) | set(self.vis_by_vehicle))),
                'pos_m': [round(tp[0]), round(tp[1])],
                'dist': round(dist),
                'speed_kn': round(spd * 1.94384, 1),
                'moving': spd >= 1.0,
                'flight': round(flight, 2),
                'lead': round(math.hypot(*lead)),
                'aim_m': [round(aim[0]), round(aim[1])],
                'brg_now': round(brg_now, 1),
                'brg_aim': round(brg_aim, 1),
                'swing': round(ang_diff(brg_aim, brg_now), 1),
                'sigma': round(sigma, 1),
                'hit_prob': round(hit_prob, 2),
            })
        # 可见的排前面，然后按距离
        out.sort(key=lambda x: (not x['visible'], x['dist']))
        return {
            'updated': time.strftime('%Y-%m-%dT%H:%M:%S'),
            'clock': round(t, 1), 'k_meters': K_METERS,
            'shell_speed': shell_speed,
            'me': {'pos_m': [round(my[0]), round(my[1])],
                   'heading_deg': me['heading'],
                   'speed_kn': me.get('speed_kn')},
            'targets': out,
        }

    # ---------------------------------------------------------- 快照
    def snapshot(self, shell_speed=DEFAULT_SHELL):
        r = self.solve(shell_speed)
        if r is None:
            r = {'updated': time.strftime('%Y-%m-%dT%H:%M:%S'),
                 'clock': round(self.clock, 1), 'k_meters': K_METERS,
                 'status': 'waiting', 'me': None, 'targets': []}
        r['status'] = 'recording'
        return r


def analyse_offline(path, shell_speed=DEFAULT_SHELL):
    la = LiveAim()
    with open(path, 'rb') as f:
        while True:
            c = f.read(65536)
            if not c:
                break
            la.feed(c)
    la.ensure_ident()
    return la.snapshot(shell_speed)


def watch(replays_dir, out_path, interval=0.5):
    temp = os.path.join(replays_dir, 'temp.wowsreplay')
    print('[*] 等待对局（%s）' % temp)
    la = LiveAim()
    while True:
        if not os.path.exists(temp):
            la = LiveAim()          # 新一局，重置
            time.sleep(interval)
            continue
        try:
            size = os.path.getsize(temp)
        except OSError:
            time.sleep(interval)
            continue
        pos = sum(len(p) for p in ([], ))
        cur = la.stream.file_pos if hasattr(la.stream, 'file_pos') else None
        # 记录已读字节数在 stream 上
        if not hasattr(la, '_read'):
            la._read = 0
        if size > la._read:
            try:
                with open(temp, 'rb') as f:
                    f.seek(la._read)
                    chunk = f.read(size - la._read)
            except OSError:
                time.sleep(interval)
                continue
            la._read += len(chunk)
            la.feed(chunk)
        la.ensure_ident()
        try:
            snap = la.snapshot()
            snap['status'] = 'recording'
            with open(out_path, 'w', encoding='utf-8') as f:
                json.dump(snap, f, ensure_ascii=False, indent=1)
            if snap['targets']:
                tg = snap['targets']
                vis = [x for x in tg if x['visible']]
                print('  t=%6.1fs  目标%d（可见%d）  最近%s %dm  飞行%.1fs 提前%dm' % (
                    snap['clock'], len(tg), len(vis),
                    (vis[0] if vis else tg[0])['name'][:12],
                    (vis[0] if vis else tg[0])['dist'],
                    (vis[0] if vis else tg[0])['flight'],
                    (vis[0] if vis else tg[0])['lead']))
            else:
                print('  t=%6.1fs  等待目标…' % snap['clock'])
        except Exception as e:
            print('  [!] %s' % e)
        time.sleep(interval)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--watch', help='replays 目录，实时监视 temp.wowsreplay')
    ap.add_argument('--replay', help='离线自检用')
    ap.add_argument('--out', default='live_aim.json')
    ap.add_argument('--shell', type=float, default=DEFAULT_SHELL)
    a = ap.parse_args()
    if a.replay:
        r = analyse_offline(a.replay, a.shell)
        print(json.dumps(r, ensure_ascii=False, indent=1)[:3000])
        return 0
    if a.watch:
        if not os.path.isdir(a.watch):
            print('[×] 目录不存在：%s' % a.watch)
            return 2
        watch(a.watch, a.out)
        return 0
    print(__doc__)
    return 0


if __name__ == '__main__':
    sys.exit(main())
