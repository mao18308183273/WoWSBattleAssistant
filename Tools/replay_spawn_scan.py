#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""WoWS 回放「AI 出生点」扫描器 —— 只依赖 Python 标准库。

作用
----
从 .wowsreplay 中还原每艘舰艇的**出生/首次出现坐标与时刻**，按剧本（scenario）
聚合成一张会越用越准的知识库 `spawn_db.json`。

为什么需要它
------------
ModsAPI 在运行中被刻意裁剪，拿不到任何坐标（已实机验证：battle 模块只有 5 个
方法，Player 对象 9 个字段里没有 position/isVisible）。但服务端下发的剧本出生点
是**固定的**，会被完整写进回放，因此可以「打一局记一笔」，越玩越精确。

回放格式（已逆向确认）
----------------------
  文件头:  magic(4)=12 32 34 11 | blockCount(i4) | jsonLen(i4) | json
          随后 blockCount-1 个 (len(i4), payload) 块
  剩余体:  Blowfish-ECB(WG 硬编码 key) + 前块链式 XOR（第 0 块丢弃） -> zlib 流
  包流:    size(u4) | type(u4) | time(f4) | payload[size]
          0x0a=Position  0x2b=PlayerPosition  0x08=EntityMethod  0x16=Version
          0x28=Map(12.6+) / 0x27=Map(<12.6)

用法
----
  python replay_spawn_scan.py                      扫描默认回放目录
  python replay_spawn_scan.py <文件或目录> [...]    扫描指定路径
  python replay_spawn_scan.py --db <path>          指定知识库输出位置
  python replay_spawn_scan.py --pve-only           只处理行动/剧情（PVE）
"""
import glob
import io
import json
import os
import pickle
import re
import struct
import sys
import zlib
from datetime import datetime

# ---------------------------------------------------------------- 常量

REPLAY_SIGNATURE = b'\x12\x32\x34\x11'
WOWS_BLOWFISH_KEY = bytes(bytearray([
    0x29, 0xB7, 0xC9, 0x09, 0x38, 0x3F, 0x84, 0x88,
    0xFA, 0x98, 0xEC, 0x4E, 0x13, 0x19, 0x79, 0xFB]))

DEFAULT_REPLAY_DIR = r'J:\Games\World_of_Warships_CN360\replays'
DEFAULT_DB = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'spawn_db.json')

# arena state 里玩家/机器人属性的编号 -> 名称映射（与客户端 15.x 一致）
ID_PROPERTY_MAP_BOTS = {
    0: 'accountDBID', 1: 'antiAbuseEnabled', 2: 'camouflageInfo', 3: 'clanColor',
    4: 'clanID', 5: 'clanTag', 6: 'crewParams', 7: 'dogTag', 8: 'fragsCount',
    9: 'friendlyFireEnabled', 10: 'id', 11: 'isAbuser', 12: 'isAlive', 13: 'isBot',
    14: 'isHidden', 15: 'isTShooter', 16: 'keyTargetMarkers',
    17: 'killedBuildingsCount', 18: 'maxHealth', 19: 'name', 20: 'realm',
    21: 'shipComponents', 22: 'shipConfigDump', 23: 'shipId', 24: 'shipParamsId',
    25: 'skinId', 26: 'teamId', 27: 'ttkStatus',
}
ID_PROPERTY_MAP = {
    0: 'accountDBID', 1: 'antiAbuseEnabled', 2: 'avatarId', 3: 'camouflageInfo',
    4: 'clanColor', 5: 'clanID', 6: 'clanTag', 7: 'crewParams', 8: 'dogTag',
    9: 'fragsCount', 10: 'friendlyFireEnabled', 11: 'id', 12: 'invitationsEnabled',
    13: 'isAbuser', 14: 'isAlive', 15: 'isBot', 16: 'isClientLoaded',
    17: 'isConnected', 18: 'isHidden', 19: 'isLeaver', 20: 'isPreBattleOwner',
    21: 'isTShooter', 22: 'keyTargetMarkers', 23: 'killedBuildingsCount',
    24: 'maxHealth', 25: 'name', 26: 'playerMode', 27: 'preBattleIdOnStart',
    28: 'preBattleSign', 29: 'prebattleId', 30: 'realm', 31: 'shipComponents',
    32: 'shipConfigDump', 33: 'shipId', 34: 'shipParamsId', 35: 'skinId',
    36: 'teamId', 37: 'ttkStatus',
}

PACKET_POSITION = 0x0a
PACKET_PLAYER_POSITION = 0x2b
PACKET_ENTITY_METHOD = 0x08
PACKET_MAP = 0x28           # 12.6+；旧版是 0x27，两者都试
PACKET_MAP_OLD = 0x27


# ---------------------------------------------------------------- Blowfish

def _pi_hex_digits(n):
    from decimal import Decimal, getcontext
    getcontext().prec = int(n * 1.21) + 40
    C = 426880 * Decimal(10005).sqrt()
    M, L, X, K, S = 1, 13591409, 1, 6, Decimal(13591409)
    for i in range(1, int(getcontext().prec / 14.18) + 8):
        M = (K ** 3 - 16 * K) * M // (i ** 3)
        L += 545140134
        X *= -262537412640768000
        S += Decimal(M * L) / X
        K += 12
    frac = (C / S) - 3
    out = []
    for _ in range(n):
        frac *= 16
        d = int(frac)
        frac -= d
        out.append(d)
    return out


def _init_boxes():
    d = _pi_hex_digits(18 * 8 + 4 * 256 * 8)
    P, S, k = [], [], 0
    for _ in range(18):
        v = 0
        for _ in range(8):
            v = (v << 4) | d[k]
            k += 1
        P.append(v)
    for _ in range(4):
        box = []
        for _ in range(256):
            v = 0
            for _ in range(8):
                v = (v << 4) | d[k]
                k += 1
            box.append(v)
        S.append(box)
    assert P[0] == 0x243F6A88 and P[1] == 0x85A308D3, 'pi 常量生成有误'
    return P, S


_PI_P, _PI_S = _init_boxes()
_MASK = 0xFFFFFFFF


class Blowfish(object):
    def __init__(self, key):
        self.P = list(_PI_P)
        self.S = [list(b) for b in _PI_S]
        self._expand(list(key))

    def _F(self, x):
        S = self.S
        return ((((S[0][x >> 24] + S[1][(x >> 16) & 0xFF]) & _MASK)
                 ^ S[2][(x >> 8) & 0xFF]) + S[3][x & 0xFF]) & _MASK

    def _enc_words(self, L, R):
        P, F = self.P, self._F
        for i in range(16):
            L ^= P[i]
            R ^= F(L)
            L, R = R, L
        L, R = R, L
        return L ^ P[17], R ^ P[16]

    def _expand(self, key):
        kl = len(key)
        k = 0
        for i in range(18):
            v = 0
            for _ in range(4):
                v = ((v << 8) | key[k % kl]) & _MASK
                k += 1
            self.P[i] ^= v
        L = R = 0
        for i in range(0, 18, 2):
            L, R = self._enc_words(L, R)
            self.P[i] = L
            self.P[i + 1] = R
        for box in self.S:
            for j in range(0, 256, 2):
                L, R = self._enc_words(L, R)
                box[j] = L
                box[j + 1] = R

    def decrypt_block(self, block):
        L, R = struct.unpack('>II', block)
        P, F = self.P, self._F
        for i in range(17, 1, -1):
            L ^= P[i]
            R ^= F(L)
            L, R = R, L
        L, R = R, L
        R ^= P[1]
        L ^= P[0]
        return struct.pack('>II', L, R)


def _blowfish_chain_decrypt(cipher):
    bf = Blowfish(WOWS_BLOWFISH_KEY)
    out = bytearray()
    prev = 0
    end = len(cipher) - len(cipher) % 8
    for i in range(0, end, 8):
        if i == 0:
            continue                      # 第 0 块丢弃（WG 的实现如此）
        (cur,) = struct.unpack('<q', bf.decrypt_block(cipher[i:i + 8]))
        cur ^= prev
        prev = cur
        out += struct.pack('<q', cur)
    return bytes(out)


# ---------------------------------------------------------------- 容器

def read_replay(path):
    """返回 (header_json, 解包后的原始字节流)。"""
    with open(path, 'rb') as f:
        if f.read(4) != REPLAY_SIGNATURE:
            raise ValueError('不是合法的 .wowsreplay: %s' % path)
        blocks_count, = struct.unpack('<i', f.read(4))
        size, = struct.unpack('<i', f.read(4))
        header = json.loads(f.read(size).decode('utf-8'))
        for _ in range(max(0, blocks_count - 1)):
            n, = struct.unpack('<i', f.read(4))
            f.read(n)
        cipher = f.read()
    return header, zlib.decompress(_blowfish_chain_decrypt(cipher))


# ---------------------------------------------------------------- pickle 兜底

class _Stub(object):
    def __init__(self, *a, **kw):
        self.__dict__.update(kw)

    def __repr__(self):
        return '<stub %s>' % self.__class__.__name__


class _ArenaUnpickler(pickle.Unpickler):
    """WG 的 arena state 里混了几个客户端自定义类，这里一律用占位对象顶掉。

    服务端仍在用 Python 2 风格的 pickle（protocol 2，8-bit 字符串），
    必须指定 encoding='bytes' 才能正确解出中文舰长名。
    """

    def __init__(self, file):
        pickle.Unpickler.__init__(self, file, encoding='bytes', errors='replace')

    def find_class(self, module, name):
        if isinstance(module, bytes):
            module = module.decode('utf-8', 'replace')
        if module in ('CamouflageInfo', 'PlayerModeDef', 'PlayerMode',
                      'constants', 'shared_utils'):
            return type(str(name), (_Stub,), {})
        try:
            return super(_ArenaUnpickler, self).find_class(module, name)
        except Exception:
            return type(str(name), (_Stub,), {})


def _try_unpickle(buf):
    """在一段字节里找出所有 protocol>=2 的 pickle 并解出来。"""
    found = []
    for m in re.finditer(rb'\x80[\x02-\x05]', buf):
        try:
            obj = _ArenaUnpickler(io.BytesIO(buf[m.start():])).load()
        except Exception:
            continue
        found.append(obj)
    return found


def _iter_ship_records(obj, depth=0):
    """递归下钻，找出形如 [(int, value), ...] 且字段数 >= 3 的舰艇档案。"""
    if depth > 4:
        return
    if isinstance(obj, (list, tuple)):
        if (len(obj) >= 3 and all(isinstance(x, (list, tuple)) and len(x) == 2
                                  and isinstance(x[0], int) for x in obj)):
            yield obj
            return
        for x in obj:
            for r in _iter_ship_records(x, depth + 1):
                yield r


def _decode_players(rows):
    """把 [(key, value)] 解成 dict；自动判断是玩家表还是机器人表。"""
    keys = [r[0] for r in rows if isinstance(r[0], int)]
    if not keys:
        return {}
    pmap = ID_PROPERTY_MAP if max(keys) > 27 else ID_PROPERTY_MAP_BOTS
    out = {}
    for k, v in rows:
        if isinstance(v, bytes):
            try:
                v = v.decode('utf-8')
            except Exception:
                v = repr(v)
        out[pmap.get(k, 'k%d' % k)] = v
    return out


# ---------------------------------------------------------------- 包流扫描

def scan_packets(data):
    """扫一遍包流，返回 (first_seen, map_name)。

    first_seen: shipId -> {'t': float, 'x': float, 'z': float}
    """
    n = len(data)
    pos = 0
    first_seen = {}
    map_name = None
    while pos + 12 <= n:
        size, ptype, ptime = struct.unpack_from('<IIf', data, pos)
        body = data[pos + 12: pos + 12 + size]
        pos += 12 + size
        if len(body) < size:
            break

        if ptype == PACKET_POSITION and size >= 33:
            eid, = struct.unpack_from('<i', body, 0)
            x, y, z = struct.unpack_from('<fff', body, 8)
            if eid and eid not in first_seen:
                first_seen[eid] = {'t': round(float(ptime), 2),
                                   'x': round(float(x), 1),
                                   'z': round(float(z), 1)}
        elif ptype == PACKET_PLAYER_POSITION and size >= 32:
            e1, e2 = struct.unpack_from('<ii', body, 0)
            x, y, z = struct.unpack_from('<fff', body, 8)
            for eid in (e1, e2):
                if eid and eid not in first_seen:
                    first_seen[eid] = {'t': round(float(ptime), 2),
                                       'x': round(float(x), 1),
                                       'z': round(float(z), 1)}
        elif ptype in (PACKET_MAP, PACKET_MAP_OLD) and size >= 8:
            try:
                ln, = struct.unpack_from('<i', body, 0)
                map_name = body[4:4 + ln].decode('utf-8', 'replace')
            except Exception:
                pass
    return first_seen, map_name


def scan_entity_methods(data):
    """抓出 arena state 里的舰艇档案：shipId -> {name, teamId, isBot, ...}。"""
    n = len(data)
    pos = 0
    roster = {}
    while pos + 12 <= n:
        size, ptype, _t = struct.unpack_from('<IIf', data, pos)
        body = data[pos + 12: pos + 12 + size]
        pos += 12 + size
        if ptype != PACKET_ENTITY_METHOD or len(body) < 8:
            continue
        for obj in _try_unpickle(body):
            for rows in _iter_ship_records(obj):
                d = _decode_players(list(rows))
                sid = d.get('shipId')
                if not isinstance(sid, int) or sid <= 0:
                    continue
                name = d.get('name')
                if not isinstance(name, str):
                    continue
                rec = roster.setdefault(sid, {})
                rec.update({
                    'name': name,
                    'teamId': d.get('teamId'),
                    'isBot': d.get('isBot'),
                    'maxHealth': d.get('maxHealth'),
                    'isAlive': d.get('isAlive'),
                })
    return roster


# ---------------------------------------------------------------- 单局分析

def analyse(path):
    header, data = read_replay(path)
    first_seen, map_name = scan_packets(data)
    roster = scan_entity_methods(data)

    spawns = []
    for sid, rec in sorted(first_seen.items(), key=lambda kv: (kv[1]['t'], kv[0])):
        info = roster.get(sid, {})
        spawns.append({
            'role': info.get('name'),
            'team': info.get('teamId'),
            'isBot': info.get('isBot'),
            'maxHealth': info.get('maxHealth'),
            't': rec['t'], 'x': rec['x'], 'z': rec['z'],
        })
    spawns.sort(key=lambda s: (s['t'] or 0, -(s['x'] or 0)))
    return {
        'file': os.path.basename(path),
        'scenario': header.get('scenario'),
        'map': header.get('mapDisplayName') or map_name,
        'mapId': header.get('mapId'),
        'gameType': header.get('gameType'),
        'matchGroup': header.get('matchGroup'),
        'gameMode': header.get('gameMode'),
        'dateTime': header.get('dateTime'),
        'scenarioConfigId': header.get('scenarioConfigId'),
        'spawns': spawns,
    }


# ---------------------------------------------------------------- 聚合

def _merge_run(db, run):
    key = run['scenario'] or '%s#%s' % (run['map'], run['scenarioConfigId'])
    scen = db['scenarios'].setdefault(key, {
        'scenario': run['scenario'],
        'map': run['map'],
        'mapId': run['mapId'],
        'scenarioConfigId': run['scenarioConfigId'],
        'runs': [],
        'spawns': {},
        'ownSpawns': [],
    })
    scen['map'] = scen['map'] or run['map']
    if run['file'] not in scen['runs']:
        scen['runs'].append(run['file'])

    # 注意：'spawns' 是**持久保存**的原始样本（_finalise 不再删它），
    # 这样下一局新回放进来时能继续往同一个 role 上累加 —— 玩得越多越精确。
    spawns = scen.setdefault('spawns', {})

    for sp in run['spawns']:
        role = sp['role']
        if not role:
            continue
        # 真人玩家的昵称每局都不同，绝不能当聚合键：否则知识库会被每局昵称塞满，
        # 而且每条永远 samples=1，永远平均不出东西。统一记为"我方出生点"样本。
        if sp['isBot'] is False:
            scen.setdefault('ownSpawns', []).append({
                'file': run['file'], 't': sp['t'],
                'x': sp['x'], 'z': sp['z'], 'team': sp['team'],
            })
            continue
        rec = spawns.setdefault(role, {
            'team': sp['team'], 'isBot': sp['isBot'],
            'maxHealth': sp['maxHealth'],
            't': [], 'x': [], 'z': [],
        })
        rec['team'] = sp['team'] if sp['team'] is not None else rec['team']
        rec['isBot'] = sp['isBot'] if sp['isBot'] is not None else rec['isBot']
        rec['maxHealth'] = sp['maxHealth'] or rec['maxHealth']
        if sp['t'] is not None:
            rec['t'].append(sp['t'])
            rec['x'].append(sp['x'])
            rec['z'].append(sp['z'])


def _finalise(db):
    """把多局采样压成均值 + 波动范围，并按时间分波次。

    关键设计：'spawns'（原始样本）**保留**在库里不删，spawnList / waves / ownSpawn
    都是从它重算出来的派生结果。这样增量追加新回放时统计会被完整重算，
    而不是被下一局的空样本覆盖掉。"""
    for key in [k for k, s in db['scenarios'].items()
                if not s.get('spawns')]:
        # 一场 AI 都没有（纯真人模式，例如 domination_special）→ 对"AI 出生点"毫无价值，剔除。
        # 同时把它们的回放记进 ignored，免得每次启动都白白重扫一遍。
        ignored = db.setdefault('ignored', [])
        for f in db['scenarios'][key].get('runs', []):
            if f not in ignored:
                ignored.append(f)
        del db['scenarios'][key]

    for scen in db['scenarios'].values():
        rows = []
        for role, rec in scen.get('spawns', {}).items():
            if not rec['t']:
                continue
            n = len(rec['t'])
            rows.append({
                'role': role,
                'team': rec['team'],
                'isBot': rec['isBot'],
                'maxHealth': rec['maxHealth'],
                'samples': n,
                't': round(sum(rec['t']) / n, 1),
                'tRange': [round(min(rec['t']), 1), round(max(rec['t']), 1)],
                'x': round(sum(rec['x']) / n, 1),
                'z': round(sum(rec['z']) / n, 1),
                'xRange': [round(min(rec['x']), 1), round(max(rec['x']), 1)],
                'zRange': [round(min(rec['z']), 1), round(max(rec['z']), 1)],
            })
        rows.sort(key=lambda r: r['t'])

        # 按 30 秒间隔切波次
        waves, cur = [], []
        for r in rows:
            if cur and r['t'] - cur[-1]['t'] > 30:
                waves.append(cur)
                cur = []
            cur.append(r)
        if cur:
            waves.append(cur)
        scen['waves'] = [
            {'index': i + 1, 't': round(sum(r['t'] for r in w) / len(w), 1),
             'count': len(w), 'ships': [r['role'] for r in w]}
            for i, w in enumerate(waves)
        ]
        scen['spawnList'] = rows

        # 我方（真人）出生点：跨局压成一个带分布范围的基准点
        own = [o for o in scen.get('ownSpawns', [])
               if o.get('x') is not None and o.get('z') is not None]
        if own:
            xs = [o['x'] for o in own]
            zs = [o['z'] for o in own]
            scen['ownSpawn'] = {
                'samples': len(own),
                'runs': len(set(o.get('file') for o in own)),
                'x': round(sum(xs) / len(xs), 1),
                'z': round(sum(zs) / len(zs), 1),
                'xRange': [round(min(xs), 1), round(max(xs), 1)],
                'zRange': [round(min(zs), 1), round(max(zs), 1)],
            }
    return db


# ---------------------------------------------------------------- 主流程

def load_db(path):
    if os.path.exists(path):
        try:
            with open(path, 'r', encoding='utf-8') as f:
                return json.load(f)
        except Exception:
            pass
    return {'generated': None, 'scenarios': {}}


def main(argv):
    db_path = DEFAULT_DB
    if '--db' in argv:
        db_path = argv[argv.index('--db') + 1]

    # 关键：--db 的取值本身不能当成回放文件/目录去扫（否则会把 json 当 .wowsreplay 打开）
    skip = set()
    for i, a in enumerate(argv):
        if a == '--db' and i + 1 < len(argv):
            skip.add(i + 1)
    args = [a for i, a in enumerate(argv[1:], start=1)
            if not a.startswith('--') and i not in skip]
    pve_only = '--pve-only' in argv
    include_all = '--all' in argv
    rebuild = '--rebuild' in argv   # 忽略"已收录"清单，用全部回放重算（改了格式时用）

    if args:
        files = []
        for a in args:
            if os.path.isdir(a):
                files += glob.glob(os.path.join(a, '*.wowsreplay'))
            else:
                files.append(a)
    else:
        files = glob.glob(os.path.join(DEFAULT_REPLAY_DIR, '*.wowsreplay'))
    files.sort()

    db = load_db(db_path)
    if rebuild:
        # 只保留 runs 清单用于去重，丢弃旧的派生结果，全部重算
        for s in db.get('scenarios', {}).values():
            s.pop('spawnList', None); s.pop('waves', None); s.pop('ownSpawn', None)
        s['spawns'] = {}
        s['ownSpawns'] = []
    known = set()
    for s in db.get('scenarios', {}).values():
        known.update(s.get('runs', []))
    # 纯真人（无 AI）的回放：已知无价值，直接跳过，不用每局重扫
    known.update(db.get('ignored', []) if not rebuild else [])

    done = skipped = failed = 0
    for path in files:
        name = os.path.basename(path)
        if name in known and not rebuild:
            skipped += 1
            continue
        try:
            run = analyse(path)
        except Exception as e:
            print('  [跳过] %s -> %s' % (name, e))
            failed += 1
            continue
        # 随机战的出生点由服务器随机挑，多局平均没有意义；默认只收 PVE（行动/剧情/人机）
        if not (run['scenario'] or run['matchGroup'] == 'pve') and not include_all:
            skipped += 1
            continue
        if pve_only and run['matchGroup'] != 'pve':
            skipped += 1
            continue
        _merge_run(db, run)
        done += 1
        print('  [OK] %-64s %s 舰艇%d' % (name[:64], run['scenario'] or run['map'],
                                          len(run['spawns'])))

    db['generated'] = datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    db = _finalise(db)
    with open(db_path, 'w', encoding='utf-8') as f:
        json.dump(db, f, ensure_ascii=False, indent=1)

    print('\n新增 %d 局，跳过 %d，失败 %d' % (done, skipped, failed))
    print('知识库: %s' % db_path)
    for key, s in db['scenarios'].items():
        print('  %-52s %2d 局  %2d 个出生点  %d 波' % (
            key[:52], len(s['runs']), len(s.get('spawnList', [])), len(s.get('waves', []))))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
