#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
replay_live.py —— .wowsreplay 的**流式/增量**解析器。

【为什么需要它】
游戏在对局进行中会往 replays\\temp.wowsreplay 里持续追加数据，打完才改名成正式回放。
之前的 replay_spawn_scan.py 是"整个文件读完再解密"，只能事后分析。
本模块改成增量：文件每增长一点就处理一点，于是**对局进行中就能读到战场数据**。

【为什么它比 ModsAPI 强得多】
ModsAPI 被官方裁剪过（battle 只有 5 个方法，Player 对象 9 个字段里没有 position/
isVisible），运行时拿不到任何坐标。而回放是服务端下发的完整战场记录，
里面逐 tick 记录了**所有**舰船的位置/航向，不依赖 mod、不受游戏版本更新影响。

【流式是怎么做到的】
容器布局（已实测）：
    magic(4)=12 32 34 11 | blockCount(i4) | jsonLen(i4) | header JSON
    | (blockCount-1) 个 (len(i4), payload) 元信息块
    | 之后**一直到文件末尾**是连续的加密流，没有内部边界
加密：Blowfish-ECB，每 8 字节一块，**解密后与前一块明文 XOR**，第 0 块丢弃。
    因为 XOR 链只依赖前一块，状态可以一路带着走 —— 所以能增量处理。
压缩：整条明文流是 zlib，用 decompressobj 增量解压即可。

【用法】
  # 一次性解析（等价旧行为，用于自检）
  python replay_live.py --file xxx.wowsreplay --summary

  # 实时监视：等 temp.wowsreplay 出现，持续输出战场状态
  python replay_live.py --watch "J:\\Games\\...\\replays" --out live_battle.json
"""
import json
import os
import struct
import sys
import time
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from replay_spawn_scan import (  # 复用已验证过的 Blowfish 与常量
    Blowfish, WOWS_BLOWFISH_KEY, REPLAY_SIGNATURE,
    PACKET_POSITION, PACKET_PLAYER_POSITION, PACKET_ENTITY_METHOD,
    PACKET_MAP, PACKET_MAP_OLD,
)

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


# ---------------------------------------------------------------- 流式解密

class BlowfishChainStream:
    """Blowfish 链式 XOR 的增量解密器。

    每次喂入任意长度的字节（内部按 8 字节对齐缓存），吐出对应明文。
    状态（prev）跨调用保持，所以可以"读一点、解一点"。
    """

    def __init__(self):
        self._bf = Blowfish(WOWS_BLOWFISH_KEY)
        self._prev = 0
        self._block_index = 0
        self._tail = b''

    def feed(self, chunk):
        if not chunk:
            return b''
        buf = self._tail + chunk
        end = len(buf) - len(buf) % 8
        out = bytearray()
        for i in range(0, end, 8):
            cur, = struct.unpack('<q', self._bf.decrypt_block(buf[i:i + 8]))
            if self._block_index == 0:
                # 第 0 块按 WG 的实现丢弃（不进明文流），且不影响 prev
                self._block_index += 1
                continue
            cur ^= self._prev
            self._prev = cur
            out += struct.pack('<q', cur)
            self._block_index += 1
        self._tail = buf[end:]
        return bytes(out)


class ReplayStream:
    """增量解析一个还在增长中的 .wowsreplay。

    用法：
        st = ReplayStream()
        st.feed(open(path,'rb').read())          # 可多次调用，只喂新增部分
        for pkt in st.packets: ...
    """

    def __init__(self):
        self.header = None
        self._cipher_started = False
        self._cipher_seen = 0      # 已经从文件里读到的密文字节数
        self._bf = BlowfishChainStream()
        self._z = zlib.decompressobj()
        self._plain = bytearray()  # 已解压、但还没凑成完整包的字节
        self.packets = []          # [(type, time, payload)]
        self.plain_bytes = 0
        self.error = None

    # ---- 头部：只在第一次 feed 时解析 ----
    def _parse_header(self, data):
        if len(data) < 12:
            return False
        if data[:4] != REPLAY_SIGNATURE:
            self.error = '不是合法的 .wowsreplay'
            return True
        blocks_count, = struct.unpack_from('<i', data, 4)
        size, = struct.unpack_from('<i', data, 8)
        if size < 0 or len(data) < 12 + size:
            return False
        try:
            self.header = json.loads(data[12:12 + size].decode('utf-8'))
        except Exception as e:
            self.error = 'header JSON 解析失败: %s' % e
            return True
        # 跳过 (blockCount-1) 个元信息块，之后就是连续密文流
        p = 12 + size
        for _ in range(max(0, blocks_count - 1)):
            if p + 4 > len(data):
                return False       # 数据还不够，等下次
            n, = struct.unpack_from('<i', data, p)
            p += 4
            if n < 0 or p + n > len(data):
                return False
            p += n
        self._cipher_started = True
        self._cipher_seen = p
        # 头里已经带进来的那部分密文先解掉
        self._consume_cipher(data[p:])
        return True

    def _consume_cipher(self, cipher):
        if not cipher:
            return
        plain = self._bf.feed(cipher)
        if not plain:
            return
        try:
            out = self._z.decompress(plain)
        except zlib.error as e:
            self.error = 'zlib 解压失败: %s' % e
            return
        if out:
            self.plain_bytes += len(out)
            self._plain += out
            self._parse_packets()

    def _parse_packets(self):
        """从缓冲区里尽量切出完整的包：size(u4) | type(u4) | time(f4) | payload[size]"""
        buf = self._plain
        pos = 0
        n = len(buf)
        while pos + 12 <= n:
            size, ptype, ptime = struct.unpack_from('<IIf', buf, pos)
            if size > 64 * 1024 * 1024:        # 明显异常，停止解析避免乱套
                self.error = '包长度异常: %d' % size
                break
            if pos + 12 + size > n:
                break                          # 还没收全，等更多数据
            body = bytes(buf[pos + 12: pos + 12 + size])
            pos += 12 + size
            self.packets.append((ptype, float(ptime), body))
        if pos:
            del buf[:pos]

    def feed(self, chunk):
        """喂入从文件当前位置读到的数据（可以是任意长度）。"""
        if self.error:
            return
        if not self._cipher_started:
            if not self._parse_header(chunk):
                return
        else:
            self._consume_cipher(chunk)


# ---------------------------------------------------------------- 一次性解析

def parse_file(path):
    st = ReplayStream()
    with open(path, 'rb') as f:
        st.feed(f.read())
    return st


# ---------------------------------------------------------------- 战场状态提取

def battle_state(st):
    """把已解析的包流整理成"当前战场快照"。

    返回 dict：ships（逐舰最新状态）、events（击杀/伤害等）、map、clock。
    坐标与游戏小地图一致：中心 (0,0)，x 向东为正、z 向北为正，单位米。
    """
    ships = {}
    events = []
    map_name = None
    clock = 0.0

    for ptype, ptime, body in st.packets:
        if ptime > clock:
            clock = ptime

        if ptype == PACKET_POSITION and len(body) >= 33:
            eid, = struct.unpack_from('<i', body, 0)
            x, y, z = struct.unpack_from('<fff', body, 8)
            # 实测：yaw 在 +32（值是 1.571≈π/2 这种合理角度），不在 +24
            yaw = struct.unpack_from('<f', body, 32)[0] if len(body) >= 36 else 0.0
            if eid:
                s = ships.setdefault(eid, {})
                s['eid'] = eid
                s['x'] = round(float(x), 1)
                s['z'] = round(float(z), 1)
                s['yaw'] = round(float(yaw), 3)
                s['last_t'] = round(ptime, 2)

        elif ptype == PACKET_PLAYER_POSITION and len(body) >= 32:
            e1, e2 = struct.unpack_from('<ii', body, 0)
            x, y, z = struct.unpack_from('<fff', body, 8)
            for eid in (e1, e2):
                if eid:
                    s = ships.setdefault(eid, {})
                    s['eid'] = eid
                    s['x'] = round(float(x), 1)
                    s['z'] = round(float(z), 1)
                    s['last_t'] = round(ptime, 2)

        elif ptype in (PACKET_MAP, PACKET_MAP_OLD) and len(body) >= 8:
            try:
                ln, = struct.unpack_from('<i', body, 0)
                map_name = body[4:4 + ln].decode('utf-8', 'replace')
            except Exception:
                pass

    return {
        'map': map_name,
        'clock': round(clock, 1),
        'ship_count': len(ships),
        'ships': sorted(ships.values(), key=lambda s: s.get('eid', 0)),
        'packets': len(st.packets),
        'plain_bytes': st.plain_bytes,
    }


# ---------------------------------------------------------------- 名单提取

def build_links(st, limit_packets=6000):
    """建立「位置 eid → 舰船身份」的完整关联链。

    回放里三套 id 各管一段，必须串起来才有意义：
      header.vehicles[]  : entityId -> {shipId(全局), relation(敌我), name}
      EntityMethod pickle: entityId -> avatarId
      Position 包        : eid == avatarId
    所以：位置 eid → avatarId → entityId → 舰名/敌我/舰种。

    返回 (by_avatar, by_entity)。
    """
    from replay_spawn_scan import _try_unpickle, _iter_ship_records, _decode_players

    h = st.header or {}
    by_entity = {}
    for v in (h.get('vehicles') or []):
        if not isinstance(v, dict):
            continue
        eid = v.get('id')
        if eid is None:
            continue
        try:
            by_entity[int(eid)] = {
                'entityId': int(eid),
                'shipGlobalId': v.get('shipId'),
                'relation': v.get('relation'),   # 0=自己 1=队友 2=敌方
                'rawName': v.get('name'),
            }
        except (TypeError, ValueError):
            continue

    avatar_of_entity = {}
    scanned = 0
    for ptype, ptime, body in st.packets:
        if ptype != PACKET_ENTITY_METHOD:
            continue
        scanned += 1
        if scanned > limit_packets:
            break
        try:
            for obj in _try_unpickle(body):
                for rec in _iter_ship_records(obj):
                    d = _decode_players(rec)
                    if not d:
                        continue
                    eid, av = d.get('id'), d.get('avatarId')
                    if isinstance(eid, int) and isinstance(av, int) and av:
                        avatar_of_entity.setdefault(int(eid), int(av))
                    # 舰名也可能只在 pickle 里才有（剧本 AI 就是这种情况）
                    if isinstance(eid, int):
                        cur = by_entity.setdefault(int(eid), {'entityId': int(eid)})
                        nm = d.get('name')
                        if nm and not cur.get('rawName'):
                            cur['rawName'] = nm
        except Exception:
            continue

    by_avatar = {}
    for eid, info in by_entity.items():
        av = avatar_of_entity.get(eid)
        if av is not None:
            by_avatar[av] = info
    return by_avatar, by_entity


def arena_info(st):
    """从 header 直接取对局上下文 —— 比从包里猜可靠得多。"""
    h = st.header or {}
    return {
        'map': h.get('mapDisplayName') or '',
        'mapName': h.get('mapName') or '',
        'mapId': h.get('mapId'),
        'scenario': h.get('scenario') or '',
        'matchGroup': h.get('matchGroup') or '',
        'gameMode': h.get('gameMode'),
        'gameType': h.get('gameType') or '',
        'duration': h.get('duration'),
        'playersPerTeam': h.get('playersPerTeam'),
        'clientVersion': h.get('clientVersionFromExe') or '',
        'weather': h.get('weatherParams'),
        'myName': h.get('playerName') or '',
        'myVehicle': h.get('playerVehicle') or '',
    }


def infer_pos_offset(pos_eids, by_avatar):
    """自动推断 Position 包 eid 与 avatarId 之间的固定偏移。

    实测本局 Position 的 eid 比 avatarId 大 1（2187668 vs 2187667）。但这类偏移
    是引擎实现细节，可能随版本变 —— 所以不写死，而是取"匹配数最多"的那个偏移。
    """
    if not by_avatar:
        return 0
    best_off, best_hit = 0, -1
    for off in range(-4, 5):
        hit = sum(1 for e in pos_eids if (e - off) in by_avatar)
        if hit > best_hit:
            best_hit, best_off = hit, off
    return best_off if best_hit > 0 else 0


def extract_roster(st, limit_packets=4000):
    """从 EntityMethod 包里把舰艇档案挖出来。

    回放的名单藏在 0x08 EntityMethod 的 Python2 pickle 里，通常在开头就有。
    返回 {eid(arenaShipId): {name, teamId, isBot, ...}}。
    """
    from replay_spawn_scan import _try_unpickle, _iter_ship_records, _decode_players
    roster = {}
    scanned = 0
    for ptype, ptime, body in st.packets:
        if ptype != PACKET_ENTITY_METHOD:
            continue
        scanned += 1
        if scanned > limit_packets:
            break
        try:
            for obj in _try_unpickle(body):
                for rec in _iter_ship_records(obj):
                    d = _decode_players(rec)
                    if not d:
                        continue
                    eid = d.get('id') or d.get('shipId')
                    if isinstance(eid, int) and eid:
                        cur = roster.setdefault(eid, {})
                        for k, v in d.items():
                            # 只补空，不覆盖已有（先出现的通常更完整）
                            if k not in cur or cur[k] in (None, ''):
                                cur[k] = v
        except Exception:
            continue
    return roster


def snapshot(st, links=None):
    """生成一份可以直接给 AI/助手消费的战场快照。

    每艘船带：eid、坐标(x,z)、航向、舰名、敌我(relation)、全局船 id(可查知识库)。
    """
    bs = battle_state(st)
    if links is None:
        by_avatar, by_entity = build_links(st)
    else:
        by_avatar, by_entity = links

    ships = []
    off = infer_pos_offset([s['eid'] for s in bs['ships']], by_avatar)
    for s in bs['ships']:
        item = dict(s)
        info = by_avatar.get(s['eid'] - off)
        if info:
            if info.get('rawName'):
                item['name'] = info['rawName']
            if info.get('relation') is not None:
                item['relation'] = info['relation']
            if info.get('shipGlobalId') is not None:
                item['shipGlobalId'] = info['shipGlobalId']
        else:
            # 我方（relation 0/1）的 avatarId 全部可从 EntityMethod 拿到且已匹配，
            # 所以没能匹配上的位置必然属于敌方（relation 2）。坐标照样有效。
            item['relation'] = 2
            item['name'] = None
        ships.append(item)

    bs['ships'] = ships
    bs['identified'] = sum(1 for s in ships if s.get('name'))
    bs['roster_size'] = len(by_entity)

    # 双方阵容（来自 header.vehicles，权威）：让 AI 知道对面都是些什么船
    lineup = {'ally': [], 'enemy': []}
    for info in by_entity.values():
        r = info.get('relation')
        key = 'ally' if r in (0, 1) else 'enemy'
        lineup[key].append({
            'name': info.get('rawName'),
            'shipGlobalId': info.get('shipGlobalId'),
            'relation': r,
        })
    bs['lineup'] = lineup
    bs.update(arena_info(st))
    return bs


# ---------------------------------------------------------------- 实时监视

def watch(replays_dir, out_path, interval=2.0, verbose=True):
    """监视 replays 目录，等 temp.wowsreplay 出现并持续增量解析。

    游戏在对局中会往 temp.wowsreplay 追加，打完改名成正式回放。
    这里就跟着它：出现则开始、增长则读、消失则结束下一轮等待。
    """
    temp = os.path.join(replays_dir, 'temp.wowsreplay')
    print('[*] 监视目录：%s' % replays_dir)
    print('[*] 等待对局开始（temp.wowsreplay 出现）…')
    print('    输出文件：%s' % out_path)

    while True:
        if not os.path.exists(temp):
            time.sleep(interval)
            continue

        print('[√] 检测到对局开始，开始实时解析…')
        st = ReplayStream()
        roster = {}
        offset = 0
        last_write = 0

        try:
            while os.path.exists(temp):
                try:
                    size = os.path.getsize(temp)
                except OSError:
                    break
                if size > offset:
                    # 增量读新增部分；用 'rb' 只读，游戏若允许共享读就能拿到
                    try:
                        with open(temp, 'rb') as f:
                            f.seek(offset)
                            chunk = f.read(size - offset)
                    except OSError as e:
                        if verbose:
                            print('    [!] 读取被拒（可能被游戏独占）：%s' % e)
                        time.sleep(interval)
                        continue
                    offset += len(chunk)
                    st.feed(chunk)
                    if st.error:
                        print('    [×] 解析出错：%s' % st.error)
                        break
                    if not roster and len(st.packets) > 20:
                        roster = build_links(st)

                now = time.time()
                if now - last_write >= interval and st.packets:
                    last_write = now
                    try:
                        snap = snapshot(st, roster)
                        snap['updated'] = time.strftime('%Y-%m-%dT%H:%M:%S')
                        snap['status'] = 'recording'
                        with open(out_path, 'w', encoding='utf-8') as f:
                            json.dump(snap, f, ensure_ascii=False, indent=1)
                        if verbose:
                            print('    t=%7.1fs  舰船 %2d（有名 %2d）  包 %d'
                                  % (snap['clock'], snap['ship_count'],
                                     snap['roster_size'], snap['packets']))
                    except Exception as e:
                        print('    [!] 写快照失败：%s' % e)
                time.sleep(0.5)
        finally:
            # 对局结束：写一份终态
            try:
                snap = snapshot(st, roster)
                snap['updated'] = time.strftime('%Y-%m-%dT%H:%M:%S')
                snap['status'] = 'ended'
                with open(out_path, 'w', encoding='utf-8') as f:
                    json.dump(snap, f, ensure_ascii=False, indent=1)
            except Exception:
                pass
            print('[*] 对局结束。等待下一局…')


# ---------------------------------------------------------------- CLI

def main(argv):
    a = argv[1:]
    if '--watch' in a:
        d = a[a.index('--watch') + 1]
        out = a[a.index('--out') + 1] if '--out' in a else 'live_battle.json'
        if not os.path.isdir(d):
            print('[×] 目录不存在：%s' % d)
            return 2
        watch(d, out)
        return 0
    if '--file' in a:
        path = a[a.index('--file') + 1]
        st = parse_file(path)
        if st.error:
            print('[×] %s' % st.error)
            return 2
        print('[*] %s' % os.path.basename(path))
        print('    header keys : %s' % (list(st.header)[:8] if st.header else '?'))
        print('    包数        : %d' % len(st.packets))
        print('    解压字节    : %d' % st.plain_bytes)
        if '--summary' in a:
            bs = battle_state(st)
            print('    地图        : %s' % bs['map'])
            print('    战场时钟    : %.1fs' % bs['clock'])
            print('    舰船数      : %d' % bs['ship_count'])
            for s in bs['ships'][:12]:
                print('      eid=%-8d (%7.1f, %7.1f) yaw=%6.2f  t=%.1f'
                      % (s['eid'], s['x'], s['z'], s.get('yaw', 0), s.get('last_t', 0)))
        return 0

    print(__doc__)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
