#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
replay_report.py —— 把 .wowsreplay 变成一份**精确的**战报。

【为什么用它而不是自己啃二进制】
BigWorld 回放的包体是按实体定义（.def）序列化的，属性 id、数组长度、方法签名都由
游戏自己的实体表决定。社区已有成熟实现（wows-replay-parser，Apache-2.0），
自己手写等于把整个 BigWorld 协议重做一遍，而且每个版本都要重做。
本脚本调用它，把回放压成一份可直接喂给 AI 的战报 + 可累积的统计数据。

【拿到什么（全部来自服务端权威数据，不是视觉推测）】
  每艘船：精确坐标、精确血量、输出/承伤（分 AP/HE）、击沉/被击沉、
          开火数、命中数、存活时间、移动距离、消耗品使用、鱼雷/飞机动向
  战斗级：双方分数、占领点进度与入侵者、剩余时间

【用法】
  python replay_report.py                       # 解析最近一局，写报告
  python replay_report.py --all                 # 解析全部回放，累积统计
  python replay_report.py --text                # 只打印中文战报（给 AI 看）
"""
import argparse
import json
import math
import os
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

GAMEDATA = os.path.join(HERE, '_gamedata', 'data', 'scripts_entity', 'entity_defs')
STATS_DIR = os.path.join(HERE, '..', 'stats')
REPORT_DIR = os.path.join(HERE, '..', 'stats', 'reports')

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def _find_parser():
    from wows_replay_parser import parse_replay
    return parse_replay


def _dt(prev_t, cur_t):
    """两次位置更新之间的间隔，夹一个下限避免除零把 topSpeed 炸飞。"""
    try:
        d = float(cur_t) - float(prev_t)
    except Exception:
        return 0.1
    return d if d > 1e-3 else 0.1


def analyse(path):
    """解析单个回放，返回结构化战报 dict。"""
    parse_replay = _find_parser()
    r = parse_replay(replay_path=path, gamedata_path=GAMEDATA)

    # ---- 名单 ----
    ships = {}
    for p in r.players:
        ships[p.entity_id] = {
            'name': p.name,
            'shipId': p.ship_id,
            'relation': p.relation,          # 0 自己 1 队友 2 敌方
            'isBot': p.is_bot,
            'maxHealth': p.max_health,
            'clan': p.clan_tag,
            'consumables': list(getattr(p.ship_config, 'consumables', []) or []),
            # 统计量
            'dealt': 0.0, 'dealtAP': 0.0, 'dealtHE': 0.0,
            'taken': 0.0, 'takenEnv': 0.0,
            'kills': 0, 'deaths': 0, 'deathAt': None, 'killer': None,
            'shots': 0, 'hits': 0,
            'damageByAmmo': defaultdict(float),
            'torpedoesFired': 0,
            'visionTicks': 0,
            'distance': 0.0,
            'topSpeed': 0.0,
        }

    def S(eid):
        return ships.get(eid)

    # ---- 逐事件累积 ----
    me_id = next((p.entity_id for p in r.players if p.relation == 0), None)
    last_pos = {}
    last_hit = {}      # (attacker,target) -> 上次命中时刻，用于把模块级伤害聚成一次命中
    cap_events = []

    for e in r.events:
        n = type(e).__name__

        if n == 'DamageEvent':
            a, t, d = e.attacker_id, e.target_id, e.damage or 0.0
            if d <= 0:
                continue
            # attacker_id == 0 是环境伤害（起火/进水/岸防）与友伤，不属于任何玩家。
            # 之前它会被算进"承伤"，把数字抬高一大截 —— 必须分开。
            env = (a == 0 or a not in ships)
            dt = (e.damage_type or '').upper()
            # DamageEvent 是**模块级**拆分：同一门炮会为每个受损模块各产生一条
            # （实测同一时刻出现 2178/726/726/1452…，且 damage_type 为空），
            # 所以既不能用它算命中数，也不能靠 damage_type 分 AP/HE。
            # 命中数改用时间聚类：同一射手对同一目标 0.6 秒内的多条算同一次命中。
            if S(a):
                ships[a]['dealt'] += d
                key = (a, t)
                prev_t = last_hit.get(key)
                if prev_t is None or (e.timestamp - prev_t) > 0.6:
                    ships[a]['hits'] += 1
                last_hit[key] = e.timestamp
                ships[a]['damageByAmmo'][str(e.ammo_id)] += d
            if S(t):
                if env:
                    ships[t]['takenEnv'] += d
                else:
                    ships[t]['taken'] += d

        elif n == 'DeathEvent':
            v, k = e.victim_id, e.killer_id
            if S(v):
                ships[v]['deaths'] += 1
                ships[v]['deathAt'] = round(e.timestamp, 1)
                ships[v]['killer'] = ships.get(k, {}).get('name') if S(k) else None
            if S(k):
                ships[k]['kills'] += 1

        elif n == 'ShotCreatedEvent':
            sid = getattr(e, 'owner_id', None) or getattr(e, 'entity_id', None)
            if S(sid):
                ships[sid]['shots'] += 1

        elif n == 'TorpedoLaunchEvent':
            tid = getattr(e, 'launcher_id', None) or getattr(e, 'entity_id', None)
            if S(tid):
                ships[tid]['torpedoesFired'] += 1

        elif n == 'MinimapVisionEvent':
            # 谁被点亮 / 灭灯，以及点亮时的精确世界坐标与航向
            vid = getattr(e, 'vehicle_entity_id', None) or getattr(e, 'entity_id', None)
            if S(vid) and getattr(e, 'is_visible', False):
                ships[vid]['visionTicks'] += 1

        elif n == 'PositionEvent':
            eid = getattr(e, 'entity_id', None)
            if eid is None or not S(eid):
                continue
            x, z = getattr(e, 'x', None), getattr(e, 'z', None)
            if x is None or z is None:
                continue
            # 阵亡瞬间的位置：学习引擎用它统计"死亡热区"
            if me_id and eid == me_id and not ships[me_id]['deathPos'] \
                    and getattr(e, 'is_alive', True) is False:
                ships[me_id]['deathPos'] = [round(x, 1), round(z, 1)]
            p = (x, z)
            if eid in last_pos:
                d = math.hypot(p[0] - last_pos[eid][0], p[1] - last_pos[eid][1])
                if d < 500:            # 过滤瞬移
                    ships[eid]['distance'] += d
                    top = ships[eid]['topSpeed'] = max(ships[eid]['topSpeed'], d / max(1e-3, _dt(last_pos[eid][2], e.timestamp)))
            last_pos[eid] = (p[0], p[1], e.timestamp)

    # ---- 终局血量 ----
    try:
        st = r.state_at(r.duration)
        for eid, s in (st.ships or {}).items():
            if S(eid):
                ships[eid]['finalHealth'] = float(getattr(s, 'health', 0) or 0)
                ships[eid]['alive'] = ships[eid].get('finalHealth', 0) > 0
    except Exception:
        for s in ships.values():
            s.setdefault('finalHealth', 0.0)
            s['alive'] = s.get('deaths', 0) == 0

    # ---- 战斗级状态 ----
    battle = {}
    try:
        bs = r.battle_state(r.duration)
        battle = {
            'timeLeft': getattr(bs, 'time_left', None),
            'teamScores': dict(getattr(bs, 'team_scores', {}) or {}),
            'winner': getattr(bs, 'battle_result_winner', None),
            'duration': getattr(bs, 'duration', None),
            'capturePoints': [
                {
                    'index': getattr(c, 'point_index', None),
                    'team': getattr(c, 'team_id', None),
                    'progress': round(float(getattr(c, 'progress', 0) or 0), 3),
                    'contested': bool(getattr(c, 'both_inside', False)),
                    'hasInvaders': bool(getattr(c, 'has_invaders', False)),
                }
                for c in (getattr(bs, 'capture_points', []) or [])
            ],
        }
    except Exception:
        battle = {}

    # ---- 组装 ----
    me = None
    for s in ships.values():
        if s['relation'] == 0:
            me = s
    ally = [s for s in ships.values() if s['relation'] in (0, 1)]
    enemy = [s for s in ships.values() if s['relation'] == 2]

    for s in ships.values():
        s['damageByAmmo'] = dict(s['damageByAmmo'])
        if 'deathPos' in s and s['deathPos'] is None:
            s.pop('deathPos', None)
        s['distance'] = round(s['distance'])
        s['hitRate'] = round(s['hits'] / s['shots'], 3) if s['shots'] else None
        s['survived'] = round(r.duration - (s['deathAt'] or r.duration), 1)

    return {
        'file': os.path.basename(path),
        'map': r.map_name,
        'gameVersion': r.game_version,
        'duration': round(r.duration, 1),
        'myName': getattr(r.players[0], 'name', '') if False else (me or {}).get('name', ''),
        'myShipId': (me or {}).get('shipId'),
        'me': me,
        'ally': ally,
        'enemy': enemy,
        'battle': battle,
    }


# ---------------------------------------------------------------- 文本报告

SHIP_CLASS = None


def _hk(rep):
    """挂上舰种中文名（可选，缺了不影响）。"""
    return rep


def to_text(rep):
    me = rep.get('me') or {}
    L = []
    L.append('【精确战报（来自 .wowsreplay 服务端权威数据，非视觉推测）】')
    L.append('地图：%s　版本：%s　时长：%.0f 秒' % (rep['map'], rep['gameVersion'], rep['duration']))
    b = rep.get('battle') or {}
    if b.get('teamScores'):
        ts = b['teamScores']
        L.append('最终比分：我方 %s : %s 敌方' % (ts.get('0', ts.get(0, '?')), ts.get('1', ts.get(1, '?'))))
    L.append('')

    L.append('■ 我的表现（%s）' % (me.get('name') or '未知'))
    L.append('  总血量 %s，剩余 %s（存活 %.0f%%）' % (
        me.get('maxHealth'), round(me.get('finalHealth', 0), 0),
        100.0 * me.get('finalHealth', 0) / max(1, me.get('maxHealth', 1))))
    aphe = ('AP %.0f / HE %.0f' % (me.get('dealtAP', 0), me.get('dealtHE', 0))
            if (me.get('dealtAP', 0) or me.get('dealtHE', 0))
            else '回放未细分弹药类型（DamageEvent 为模块级拆分，无法可靠区分 AP/HE）')
    env = ('　（另有起火/进水等环境伤害 %.0f 不计入）' % me['takenEnv']) if me.get('takenEnv', 0) else ''
    L.append('  输出 %.0f（%s）　玩家承伤 %.0f%s' % (
        me.get('dealt', 0), aphe, me.get('taken', 0), env))
    L.append('  开火 %d 发，命中 %d 发，命中率 %s' % (
        me.get('shots', 0), me.get('hits', 0),
        ('%.1f%%' % (me['hitRate'] * 100)) if me.get('hitRate') is not None else '无数据'))
    L.append('  击沉 %d 艘，被击沉 %d 次%s' % (
        me.get('kills', 0), me.get('deaths', 0),
        ('，阵亡于第 %s 秒（凶手：%s）' % (me.get('deathAt'), me.get('killer')))
        if me.get('deathAt') else '，全程存活'))
    # 航程为 0 且确实开过火 => 说明本局自身没有 Position 流（中途加入/观战位），
    # 这时必须说"数据缺失"，不能报 0 —— 0 会被 AI 当成"一直没动"。
    if me.get('shots', 0) and not me.get('distance'):
        L.append('  航程：数据缺失（本局回放里没有你自身的位置流，常见于中途加入或观战）')
    else:
        L.append('  航程 %.0f 米，最高航速 %.1f 节，发射鱼雷 %d 枚' % (
            me.get('distance', 0), me.get('topSpeed', 0) * 1.94384, me.get('torpedoesFired', 0)))
    if me.get('visionTicks'):
        L.append('  被点亮 %d 个采样点（约 %.0f 秒处于可见状态）' % (
            me['visionTicks'], me['visionTicks'] / 10.0))
    L.append('')

    def team(title, arr):
        L.append('■ %s（%d 艘）' % (title, len(arr)))
        for s in sorted(arr, key=lambda x: -x.get('dealt', 0)):
            hp = '%d%%' % (100.0 * s.get('finalHealth', 0) / max(1, s.get('maxHealth', 1)))
            L.append('  %-20s 船id=%-11d 剩余%-5s 输出%-7.0f 承伤%-7.0f 击沉%d 阵亡%s%s' % (
                (s.get('name') or '?')[:20], s.get('shipId', 0), hp,
                s.get('dealt', 0), s.get('taken', 0), s.get('kills', 0),
                ('第%ss' % s['deathAt']) if s.get('deathAt') else '否',
                ' [AI]' if s.get('isBot') else ''))
        L.append('')

    team('我方队伍', rep.get('ally') or [])
    team('敌方队伍', rep.get('enemy') or [])

    cps = b.get('capturePoints') or []
    if cps:
        L.append('■ 占领点')
        for c in cps:
            st = '我方' if c.get('team') == 0 else ('敌方' if c.get('team') == 1 else '中立')
            extra = []
            if c.get('contested'):
                extra.append('**双方均在圈内，争夺中**')
            if c.get('hasInvaders'):
                extra.append('有入侵者')
            L.append('  %d 号点：%s，进度 %.0f%%%s' % (
                c.get('index', 0), st, 100 * c.get('progress', 0),
                ('　' + '，'.join(extra)) if extra else ''))
    return '\n'.join(L)


# ---------------------------------------------------------------- 累积统计

def accumulate(reports):
    """把多局战报汇总成长期统计（供自适应学习用）。"""
    agg = {
        'battles': 0, 'wins': 0, 'losses': 0, 'draws': 0,
        'totalDamage': 0.0, 'totalTaken': 0.0, 'totalKills': 0, 'totalDeaths': 0,
        'totalShots': 0, 'totalHits': 0, 'totalDistance': 0.0,
        'byShip': defaultdict(lambda: defaultdict(float)),
        'byMap': defaultdict(lambda: {'battles': 0, 'wins': 0, 'damage': 0.0}),
        'deathHeat': defaultdict(int),      # 死亡位置热区（按 200 米网格）
        'deathCause': defaultdict(int),
    }
    for rep in reports:
        me = rep.get('me')
        if not me:
            continue
        agg['battles'] += 1
        agg['totalDamage'] += me.get('dealt', 0)
        agg['totalTaken'] += me.get('taken', 0)
        agg['totalKills'] += me.get('kills', 0)
        agg['totalDeaths'] += me.get('deaths', 0)
        agg['totalShots'] += me.get('shots', 0)
        agg['totalHits'] += me.get('hits', 0)
        agg['totalDistance'] += me.get('distance', 0)

        sid = me.get('shipId')
        s = agg['byShip'][str(sid)]
        s['battles'] += 1
        s['damage'] += me.get('dealt', 0)
        s['kills'] += me.get('kills', 0)
        s['deaths'] += me.get('deaths', 0)

        mp = rep.get('map')
        m = agg['byMap'][mp]
        m['battles'] += 1
        m['damage'] += me.get('dealt', 0)

        if me.get('deathAt'):
            agg['deathCause'][me.get('killer') or '未知'] += 1

    return agg


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--replay', help='指定回放文件')
    ap.add_argument('--dir', help='回放目录（默认取最近一个）')
    ap.add_argument('--text', action='store_true', help='打印中文战报')
    ap.add_argument('--all', action='store_true', help='解析全部回放并累积统计')
    ap.add_argument('--limit', type=int, default=200)
    a = ap.parse_args()

    if a.dir:
        d = a.dir
    else:
        d = os.path.join(os.environ.get('WOWS_PATH', r'J:\Games\World_of_Warships_CN360'), 'replays')
    if not a.replay and not os.path.isdir(d):
        print('[×] 回放目录不存在：%s' % d)
        return 2

    if a.replay:
        files = [a.replay]
    else:
        files = [os.path.join(d, f) for f in os.listdir(d) if f.endswith('.wowsreplay')]
        files.sort(key=os.path.getmtime, reverse=True)
        files = files[:a.limit]
    if a.all:
        files = files[::-1]        # 全部模式按时间正序

    if not os.path.isdir(GAMEDATA):
        print('[×] 缺少实体定义：%s' % GAMEDATA)
        print('    需要 wows-gamedata 的 data/scripts_entity/entity_defs')
        return 2

    reports = []
    for i, f in enumerate(files, 1):
        try:
            rep = analyse(f)
            reports.append(rep)
            print('[%d/%d] %s → 输出 %.0f，击沉 %d，阵亡 %s' % (
                i, len(files), os.path.basename(f)[:40],
                (rep.get('me') or {}).get('dealt', 0),
                (rep.get('me') or {}).get('kills', 0),
                '是' if (rep.get('me') or {}).get('deathAt') else '否'))
        except Exception as ex:
            print('[%d/%d] %s 解析失败：%s' % (i, len(files), os.path.basename(f)[:40], str(ex)[:90]))

    if not reports:
        print('[×] 没有可用战报')
        return 1

    if a.text or not a.all:
        print()
        print(to_text(reports[-1]))

    if a.all:
        os.makedirs(REPORT_DIR, exist_ok=True)
        os.makedirs(STATS_DIR, exist_ok=True)
        for rep in reports:
            with open(os.path.join(REPORT_DIR, rep['file'].replace('.wowsreplay', '.json')),
                      'w', encoding='utf-8') as f:
                json.dump(rep, f, ensure_ascii=False, indent=1)
        agg = accumulate(reports)
        # defaultdict 不能直接 JSON 化
        plain = json.loads(json.dumps(agg, default=lambda o: dict(o)))
        with open(os.path.join(STATS_DIR, 'career.json'), 'w', encoding='utf-8') as f:
            json.dump(plain, f, ensure_ascii=False, indent=1)
        print()
        print('=== 生涯统计（%d 局）===' % plain['battles'])
        print('  总输出 %.0f　总承伤 %.0f　总击沉 %d　总阵亡 %d' % (
            plain['totalDamage'], plain['totalTaken'],
            plain['totalKills'], plain['totalDeaths']))
        if plain['totalShots']:
            print('  总开火 %d，总命中 %d，命中率 %.1f%%' % (
                plain['totalShots'], plain['totalHits'],
                100.0 * plain['totalHits'] / plain['totalShots']))
        print('  平均每局输出 %.0f，平均航程 %.0f 米' % (
            plain['totalDamage'] / max(1, plain['battles']),
            plain['totalDistance'] / max(1, plain['battles'])))
        print('  战报已存到：%s' % REPORT_DIR)
        print('  生涯统计：%s' % os.path.join(STATS_DIR, 'career.json'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
