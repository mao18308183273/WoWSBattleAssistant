# -*- coding: utf-8 -*-
# WowsBAMod v2 - WoWSBattleAssistant 官方 ModsAPI 数据采集 + 能力自省 + 游戏内 HUD
#
# 沙箱约束（实测）：
#   * 可用模块白名单：xml, copy, struct, Keys, Math, datetime, re, math, time, array, SpatialUI, xml.dom
#   * 禁 os / json / sys / threading；dir() 对 API 对象返回 []；open(path,'a') 被拦（只能 'w' 整写）
#   * API 通过 builtins 注入：events/callbacks/battle/dataHub/utils/constants/input/ui/web/
#     flash/dock/replay/contentSdk/customPorts/devmenu；无需 import
#   * 所有入口都包 try/except，绝不抛出未捕获异常
#
# 输出到 <mod_dir>\data\，外部助手只读该目录：
#   mod_ready.json     连接状态/心跳（30s）
#   battle.json        对局开始     battle_end.json  对局结束
#   state.json         实时快照：双方血量/存活/个人计数（每 PLAYERS_INTERVAL 秒）
#   players.json       实时花名册：全员权威 teamId/isBot/isAlive/全局船Id/内部船名/等级
#   events.jsonl       事件流（缎带/炮弹/成就/SFM）
#   stats_parsed.json  战后统计（摘要）
#   stats_full.json    战后统计（完整 interactions 分类明细）
#   capabilities.json  运行期能力自省（API 成员/模块可达性/事件名命中）
#   hud_state.json     游戏内 HUD 状态与自检结果
import time

MOD_NAME = 'WowsBAMod'
API_VERSION = 'API_v1.0'

RETRY_INTERVAL = 8
HEARTBEAT_INTERVAL = 30
PLAYERS_INTERVAL = 2.0          # 花名册/状态落盘间隔（秒）
CAPS_DELAY = 12.0               # 进对局后多少秒做能力自省（等战斗 UI 就绪）

# HUD 模式：0=完全不碰  1=只枚举成员(安全,默认)  2=尝试创建图元并绘制(可能让游戏报错,请先在训练房试)
# 建议：先用 1 打一局，看 data\hud_state.json 的 members/types，确认 API 后再改成 2。
HUD_MODE = 1

# ---------- 路径（无 os 模块，靠 __file__ 推导）----------
try:
    _mf = __file__
    if '\\' in _mf:
        _mod_dir = _mf.rsplit('\\', 1)[0]
    else:
        _mod_dir = _mf.rsplit('/', 1)[0]
except Exception:
    _mod_dir = ''
_data_dir = _mod_dir + '\\data' if _mod_dir else 'data'


# ---------- 极简 JSON 写出（禁 json 模块）----------
def _js(v):
    if v is None:
        return 'null'
    if v is True:
        return 'true'
    if v is False:
        return 'false'
    t = type(v)
    if t is int:
        return repr(v)
    if t is long:
        return '%d' % v                      # 关键修复：repr(long) 带 'L' 后缀 -> JSON 非法
    if t is float:
        if v != v:
            return 'null'
        if v == float('inf') or v == float('-inf'):
            return 'null'
        return repr(v)
    if t is unicode:
        try:
            v = v.encode('utf-8')
        except Exception:
            return _js(repr(v))
    if t is str or t is unicode:
        out = []
        for ch in v:
            if ch == '"':
                out.append('\\"')
            elif ch == '\\':
                out.append('\\\\')
            elif ch == '\n':
                out.append('\\n')
            elif ch == '\r':
                out.append('\\r')
            elif ch == '\t':
                out.append('\\t')
            elif ord(ch) < 32:
                out.append('\\u%04x' % ord(ch))
            else:
                out.append(ch)
        return '"' + ''.join(out) + '"'
    if t is list or t is tuple:
        return '[' + ','.join([_js(x) for x in v]) + ']'
    if t is dict:
        parts = []
        for k, val in v.items():
            parts.append(_js(k if isinstance(k, (str, unicode)) else str(k)) + ':' + _js(val))
        return '{' + ','.join(parts) + '}'
    try:
        if hasattr(v, 'co_name'):
            return _js('<code %s>' % v.co_name)
    except Exception:
        pass
    try:
        return _js(v.__class__.__name__)     # 兜底：只记类型名，避免无限递归
    except Exception:
        return 'null'


def _write(name, obj):
    try:
        f = open(_data_dir + '\\' + name, 'w')
        f.write(_js(obj))
        f.close()
        return True
    except Exception:
        return False


def _clear_file(name):
    try:
        f = open(_data_dir + '\\' + name, 'w')
        f.close()
    except Exception:
        pass


def _now():
    try:
        return time.strftime('%Y-%m-%d %H:%M:%S')
    except Exception:
        return str(time.time())


# ---------- 事件缓冲（沙箱禁 append，只能内存缓冲 + 'w' 整写）----------
_events_buffer = []
_EVENTS_MAX = 300


def _event(obj):
    global _events_buffer
    try:
        _events_buffer.append(obj)
        if len(_events_buffer) > _EVENTS_MAX:
            _events_buffer = _events_buffer[-_EVENTS_MAX:]
        _flush_events()
    except Exception:
        pass


def _flush_events():
    try:
        f = open(_data_dir + '\\events.jsonl', 'w')
        for e in _events_buffer:
            f.write(_js(e) + '\n')
        f.close()
    except Exception:
        pass


# ---------- 运行状态 ----------
_connected = False
_subscribed_ok = 0
_subscribed_names = set()
_last_heartbeat = 0.0
_last_retry = 0.0
_battle_active = False
_diagnosed = False
_caps_done = False
_caps_at = 0.0
_caps_phase = 0

_players_last = 0.0
_shipinfo_cache = {}
_team_of = {}
_my_eid = None
_my_team = None
# 【关键】炮弹事件的 shooterId / victimId 用的是 arenaShipId 命名空间，
# 与 entityId 完全不同（实测：entityId=87189 而 arenaShipId=1860726）。
# 以前拿 entityId 去比对，导致 damageDealt / damageReceived / hits 恒为 0。
_arena_team_of = {}
_my_arena = None
_live = {'damageDealt': 0, 'damageReceived': 0, 'frags': 0, 'shots': 0,
         'hits': 0, 'torp_hits': 0, 'fires': 0, 'floods': 0, 'citadels': 0}

_hud = {'mode': HUD_MODE, 'ok': False, 'draws': 0, 'members': {}}


def _get_builtin(name):
    try:
        bi = __builtins__
        if isinstance(bi, dict):
            return bi.get(name)
        return getattr(bi, name, None)
    except Exception:
        return None


def _safe_get(o, n, d=None):
    try:
        return getattr(o, n)
    except Exception:
        return d


# ---------- 订阅 + 一次性诊断 ----------
def _subscribe():
    global _diagnosed
    ev = _get_builtin('events')
    diag = {'subscribe': {}, 'api_members': {}}
    try:
        diag['subscribe']['events_obj'] = ('OK type=' + type(ev).__name__) if ev is not None else 'None'
        if ev is not None:
            diag['subscribe']['events_members'] = sorted([n for n in dir(ev) if not n.startswith('_')])
    except Exception as e:
        diag['subscribe']['events_members'] = 'ERR ' + repr(e)[:120]

    ok = 0
    for name, cb in _EVENT_CANDIDATES:
        if name in _subscribed_names:
            ok += 1
            continue
        try:
            fn = getattr(ev, name, None) if ev is not None else None
            if fn is None:
                diag['subscribe'][name] = 'NO ATTR'
                continue
            fn(cb)
            _subscribed_names.add(name)
            ok += 1
            diag['subscribe'][name] = 'OK'
        except Exception as e:
            diag['subscribe'][name] = 'ERR ' + repr(e)[:100]

    try:
        cb_api = _get_builtin('callbacks')
        if cb_api is not None and hasattr(cb_api, 'perTick'):
            cb_api.perTick(_watchdog_tick)
            diag['subscribe']['watchdog'] = 'perTick OK'
        else:
            diag['subscribe']['watchdog'] = 'callbacks None/no perTick: ' + repr(cb_api)[:100]
    except Exception as e:
        diag['subscribe']['watchdog'] = 'ERR ' + repr(e)[:100]

    if not _diagnosed:
        _diagnosed = True
        for g in ('battle', 'dataHub', 'utils', 'constants', 'input', 'ui', 'web',
                  'flash', 'dock', 'replay', 'contentSdk', 'customPorts', 'devmenu', 'callbacks'):
            try:
                o = _get_builtin(g)
                diag['api_members'][g] = 'None' if o is None else (
                    'keys=' + repr(sorted([n for n in dir(o) if not n.startswith('_')])[:20]))
            except Exception as e:
                diag['api_members'][g] = 'ERR ' + repr(e)[:80]
        _write('diag.json', diag)
    return ok


def _write_ready(status):
    _write('mod_ready.json', {'ts': _now(), 'api': API_VERSION, 'status': status,
                              'subscribed': _subscribed_ok, 'mod': MOD_NAME})


def _try_connect():
    global _connected, _subscribed_ok
    try:
        _write_ready('connecting')
        n = _subscribe()
        _subscribed_ok = n
        if n > 0:
            _connected = True
            _write_ready('connected')
            print '[WowsBAMod] connected, subscribed=%d' % n
        return _connected, n
    except Exception:
        return False, 0


def _ensure_connected():
    if not _connected:
        _try_connect()


# ============================================================
#  能力自省：把 exe 里挖到的真实名字逐个 getattr，弄清沙箱到底开放了什么
# ============================================================
# 原生模块名（从 exe 挖到 / 常见命名）
_NATIVE_MODULES = ('Lesta', 'BigWorld', 'SpatialUI', 'Math', 'Keys', 'Physics',
                   'Entities', 'entityManager', 'vehicles', 'wows', 'Sound', 'GUI',
                   'BWPersonality', 'BigWorldClientScript', 'Scaleform', 'replay',
                   'contentSdk', 'constants', 'utils', 'ui', 'flash', 'battle')

# 原生 API 名字表 —— 全部来自 exe 里 PyModuleMethodLink / 类型属性表的明文名字
# （wows\source\lib\lesta\gamelogic\optimization.cpp、py_ray_cast_utils.cpp、
#   spatial_hash、physics/py_factory.cpp 等）
_NATIVE_NAMES = (
    # 实体矩阵 / 位置 / 包围盒
    'getEntityMatrix', 'getInvertedEntityMatrix', 'entMatrixCached',
    'worldBBox', 'localBBox', 'expandBoundingBox2D',
    'hull', 'hullNodeMatrixWorld', 'hullNodeMatrixLocal',
    'getWorldMatrix', 'getInvertedWorldMatrix', 'getPosition', 'linearVelocity',
    'positionToYaw', 'createAABBMinMax', 'getHeightAtPos', 'collideChunkTerrain',
    'getArmorLocalBoundingBox', 'getArmorPickedMaterial',
    'getModelLocalBoundingBox', 'getModelLocalBoundingBoxByName',
    'getModelTransform', 'setModelTransform', 'placeModel',
    # 实体集合 / 类型
    'allEntities', 'Vehicle', 'Building', 'createEntity', 'createPlayerEntity',
    'createWowsServerReplayPlayerEntity', 'collideWithVehicleEntities',
    'getChunkFreeSpace', 'getSpaceId', 'getOwner',
    # 屏幕投影 / 可见性
    'isOnScreen', 'getScreenPositionByWorldPosition',
    'getScreenPositionByWorldPositionExtended', 'getShipScreenBox',
    'worldToScreenCoords', 'screenToWorldCoords',
    # 地图边界
    'getMapBorder', 'pyGetMapBorder', 'getOriginalMapBorder', 'pyGetOriginalMapBorder',
    'setMapBorder', 'resetMapBorder',
    # 兵器 / 锁定 / 目标
    'vehicleGetTargetPos', 'gunTargetPos', 'weaponLockFlags', 'selectedWeapon',
    'weaponLocksGetLockType', 'guns', 'pitches', 'locks', 'nodeTree', 'maxDist',
    'hoopRanging', 'isPlayer', 'isBot', 'atba', 'artillery',
    'phaserLasers', 'impulseLasers', 'chargeLasers', 'TORPEDO',
    # 弹道预测
    'PyShotTrajectory', 'estimateTrajectoryTime', 'hitDistance', 'shooterHeight',
    'truePosition', 'trueDirection', 'truePitch', 'isFinished', 'remainingHitDistance',
    # 空间查询 / 射线
    'createSpatial', 'deleteSpatial', 'getSpatial', 'SpatialHash', 'rayCast',
    'findIntersectionNodesZ', 'PyBoundingBoxPtr',
    # 物理
    'PyPhysicsSimulator', 'PhysicsSimulator', 'computeEnginePower', 'computeMaxSpeed',
    'computeDragCoefA', 'getShipPhysicsDragPower', 'isSleeping', 'awake', 'volume',
    'updateState', 'correctBodyFromServer', 'applyImpulse', 'deletePhysics', 'mass',
    # 渲染设置（虚线环等）
    'setRingSettings', 'setSmokeSilhouetteSettings',
    'setCircularRingDashedSettings', 'setCircularRingSolidSettings',
    # 小船筛选
    'PyShipFilter', 'ShipFilter',
)

# 玩家/舰船对象上要额外试探的（属性 + **方法**，上一版只试了属性）
_OBJ_EXTRA = (
    # 基线：这几个是已实测存在的，放在最前面用于自证探针没跑偏
    'name', 'teamId', 'isBot', 'isOwn', 'isAlive', 'maxHealth', 'shipId', 'id',
    'level', 'shipName', 'shipInternal',
    # 位置 / 矩阵 / 屏幕投影（若这些能拿到，坐标问题就解决了）
    'getInvertedEntityMatrix', 'getEntityMatrix', 'getWorldMatrix', 'getPosition',
    'isOnScreen', 'getScreenPositionByWorldPosition', 'vehicleGetTargetPos',
    'position', 'worldPosition', 'transform', 'matrix', 'lastKnownPosition',
    # 可见性 / 点亮状态
    'isVisible', 'visible', 'spotted', 'isSpotted', 'detected', 'isDetected',
    'visibleRange', 'detectionRange', 'spottingRange',
    # 兵器 / 消耗品 / 目标
    'isPlayer', 'selectedWeapon', 'weaponLockFlags', 'atba', 'artillery',
    'guns', 'weaponType', 'ammoType', 'reloadTime', 'reloadLeft',
    'consumables', 'consumable', 'targetId', 'lockedTarget',
    # 运动 / 状态
    'speed', 'yaw', 'heading', 'course', 'currentHealth', 'health', 'hp',
    'hullHealth', 'distance', 'isSunk', 'killerId', 'clanName', 'clanTag',
    'accountId', 'dbId', 'premium', 'karma', 'agro',
)

# 坐标 / 可见性专项猜测表（v3 探测用）：只要命中任意一个，坐标问题就能解
_POS_EXTRA = (
    'worldPosition', 'lastPosition', 'lastKnownPosition', 'getPos', 'getPosition',
    'getWorldPosition', 'getLastKnownPosition', 'getVisiblePosition', 'getShipPosition',
    'isVisibleOnMinimap', 'isVisibleByPlayer', 'detectionState', 'visibility',
    'isDetectedByEnemy', 'radioLocation', 'isRadioLocated', 'getRadioLocation',
    'lastSeenPosition', 'lastSeenTime', 'ghostPosition', 'markerPosition',
    'minimapPosition', 'mapPosition', 'gridPosition', 'cellX', 'cellY',
    'spottedTime', 'isSpottedNow', 'wasSpotted', 'distanceToPlayer', 'getDistanceTo',
    'currentPosition', 'serverPosition', 'clientPosition', 'posX', 'posY', 'posZ',
    'rotation', 'direction', 'getDirection', 'orientation',
)

# flash / UI 调用猜测表：若能 invoke flash，就能直接往战斗 HUD 上写东西
_FLASH_EXTRA = (
    'call', 'invoke', 'callFlash', 'getMember', 'setMember', 'getWindow',
    'createMovie', 'loadMovie', 'movie', 'root', 'addCallback', 'registerCallback',
    'sendEvent', 'dispatchEvent', 'gfxInvoke', 'invokeFlash', 'showMessage',
    'addMessage', 'setVisible', 'setString', 'setNumber', 'setBool', 'getRoot',
    'getMovie', 'createComponent', 'getComponent',
)

_UI_NAMES = (
    'borderFading', 'outlineTransition', 'dashLength', 'borderInnerFading', 'dashSpeed',
    'dashInterval', 'depthTestBits', 'dashFading', 'brightnessInfluence', 'alphaFactor',
    'ellipseInnerRadius', 'lineWidth', 'ellipseEdgeColor', 'ellipseCenterColor',
    'outlineWidth', 'outlineColor', 'depthTestBox', 'depthTestDistance', 'spaceMode',
    'roundLineEnds', 'colorFactor', 'visible', 'remove', 'setParams', 'setTransform',
    'setMatrix', 'setTransformProvider', 'setDepthTestBoxFromWorldBox', 'setDepthSorting',
    'boxProjection', 'worldToScreenCoords', 'screenToWorldCoords',
    'taSetParams', 'taSetTransform', 'taRemove', 'taToggle', 'taSet',
    'taToggleShipsHighlighting', 'taSetShipHighlighterIndex',
    'Params', 'System', 'Mesh', 'LineStripBuilder', 'MeshUpdater', 'EllipseContourBuilder',
    'Rect', 'Box', 'Lines', 'LineStrip', 'Triangles', 'Ellipse', 'EllipseContour',
    'Regular', 'CurvedQuad', 'SingleMeshFigure', 'PyBoundingBox',
    'SM_3D', 'SM_2D', 'SM_2Dto3D', 'SM_2Dto3D_CLIPPING',
    'DT_BIT_ENABLE', 'DT_BIT_OR', 'DT_BIT_CLIP', 'DT_BIT_INVERSE',
    'DT_BIT_FAR_DISTANCE', 'DT_BIT_INSIDE_BOX', 'MAX_MESHES',
)

_CAMERA_NAMES = ('worldToScreenCoords', 'screenToWorldCoords', 'getAspectRatio',
                 'setActiveCamera', 'setActiveCameraWithBlend', 'setRotateYPR',
                 'getCameraParams', 'activeCamera', 'aspectRatio')

_DEBUG_TEXT_NAMES = ('drawText', 'addText', 'addString', 'setText', 'isOriginAtTop',
                     'inScreenSpace', 'draw', 'text')

_BATTLE_NAMES = ('getPlayersInfo', 'getSelfPlayerInfo', 'getPlayerShipInfo', 'getShipInfo',
                 'getShips', 'getEntities', 'getVehicles', 'getBattleStatistics',
                 'getBattleResult', 'getArenaInfo', 'getArenaInfoStr', 'getMinimapInfo',
                 'getPlayerInfo', 'getPlayers', 'getConsumableInfo', 'getTeamInfo',
                 'getDamageStat', 'getVisibilityInfo', 'playersInfo', 'selfPlayerInfo',
                 'getPlayerConsumables', 'getPlayerHealth', 'getEntityPosition')

_PLAYER_FIELD_NAMES = (
    'position', 'pos', 'coordinates', 'coords', 'x', 'y', 'z',
    'yaw', 'heading', 'speed', 'velocity', 'course',
    'health', 'hp', 'curHp', 'currentHealth', 'hullHealth', 'regeneratedHealth',
    'isVisible', 'visible', 'detected', 'spotted', 'isDetected',
    'burning', 'isBurning', 'flooding', 'isFlooding',
    'consumables', 'consumable', 'shipName', 'vehicleName', 'className', 'shipClass',
    'relation', 'team', 'playerName', 'userName', 'clanName', 'clanTag', 'accountId',
    'dbId', 'weaponType', 'selectedAmmo', 'reloadTime', 'reloadLeft',
    'isSunk', 'sunk', 'deathTime', 'spawnTime', 'distance', 'healthPercent',
)

_REPLAY_NAMES = ('getGunReloadAmountLeft', 'getConsumableSlotCooldownAmount', 'getFov',
                 'getArenaInfoStr', 'getBattleResults', 'getCameraParams', 'setCameraParams',
                 'getCurrentReplayFileName', 'playerVehicleID', 'turretYaw', 'gunPitch',
                 'arenaLength', 'arenaPeriod', 'isClientReady', 'recPlayerVehicleName',
                 'recMapName', 'getTimeMark', 'updateOfflinePositions', 'removeEntityFromWorld')

_EVENT_EXTRA = (
    'onBattleStarted', 'onBattleStart', 'onBattleEnd', 'onBattleQuit',
    'onBattleStatsReceived', 'onGotRibbon', 'onReceiveShellInfo', 'onAchievementEarned',
    'onSFMEvent', 'onBattleChatMessage', 'onReceiveBattleResults', 'onDamageReceived',
    'onReceiveDamageStat', 'onShipDamage', 'onShipRegen', 'onShipKill', 'onShipDestroyed',
    'onVehicleKilled', 'onVehicleVisibility', 'onSmokeCreated', 'onSmokeScreen',
    'onPlaneShotDown', 'onAircraftKilled', 'onCapturePoint', 'onTeamScore',
    'onConsumableUsed', 'onConsumable', 'onTorpedoHit', 'onMinimapVisionInfo',
    'onMinimapUpdate', 'onArenaStateReceived', 'onPlayerStateChanged', 'onEntityState',
    'onChatMessage', 'onRibbon', 'onShellInfo', 'onFlashReady', 'onUiReady',
)


def _enum_target(obj, names, limit=400):
    out = {}
    if obj is None:
        return out
    n = 0
    for name in names:
        if n >= limit:
            break
        try:
            v = getattr(obj, name)
        except Exception:
            continue
        n += 1
        try:
            if callable(v):
                out[name] = 'callable'
            elif isinstance(v, (int, long, float, bool)):
                out[name] = 'num:%s' % repr(v)[:24]
            elif isinstance(v, (str, unicode)):
                out[name] = 'str:%s' % v[:40]
            else:
                out[name] = 'obj:%s' % type(v).__name__
        except Exception:
            out[name] = '?'
    return out


def _import_module(name):
    try:
        return __import__(name), ''
    except Exception as e:
        return None, repr(e)[:90]


def _run_capabilities():
    """把沙箱真实开放面记录下来 —— 这是把'猜'变成'知道'的唯一手段。"""
    global _caps_done
    try:
        caps = {'ts': _now(), 'api_version': API_VERSION, 'hud_mode': HUD_MODE}

        # 0) 【最关键】沙箱到底往 builtins 里注入了哪些名字 —— 这一步能直接看出
        #    有没有 Lesta / BigWorld 这类能拿到实体坐标的原生模块
        try:
            if isinstance(__builtins__, dict):
                caps['builtins'] = sorted([k for k in __builtins__.keys()
                                           if not k.startswith('__')])
            else:
                caps['builtins'] = sorted([k for k in dir(__builtins__)
                                           if not k.startswith('__')])
        except Exception as e:
            caps['builtins'] = 'ERR ' + repr(e)[:100]

        mods = {}
        for name in ('SpatialUI', 'Math', 'Keys', 'BigWorld', 'GUI', 'Sound',
                     'Scaleform', 'array', 'struct', 'datetime',
                     'Lesta', 'Physics', 'Entities', 'entityManager', 'vehicles',
                     'wows', 'BWPersonality', 'BigWorldClientScript'):
            m, err = _import_module(name)
            if m is not None:
                members = []
                try:
                    members = sorted([n for n in dir(m) if not n.startswith('_')])[:60]
                except Exception:
                    pass
                mods[name] = {'import': 'OK', 'members': members}
            else:
                bi = _get_builtin(name)
                mods[name] = {'import': 'FAIL: ' + err,
                              'builtin': 'None' if bi is None else type(bi).__name__}
        caps['modules'] = mods

        # 0b) 原生 API 名字大扫荡：对每个可达模块，把 exe 里挖到的名字逐个 getattr
        #     —— 这是判断"能不能拿到坐标/可见性"的核心手段
        sweep = {}
        for mn in _NATIVE_MODULES:
            o = _get_builtin(mn)
            tag = 'builtin'
            if o is None:
                o, err = _import_module(mn)
                tag = 'import' + ('' if o is not None else ':' + err)
            if o is None:
                continue
            hit = _enum_target(o, _NATIVE_NAMES, limit=len(_NATIVE_NAMES))
            if hit:
                sweep[mn] = {'source': tag, 'hits': hit}
            else:
                sweep[mn] = {'source': tag, 'hits': 'none of the %d names'
                             % len(_NATIVE_NAMES)}
        caps['native_api_sweep'] = sweep

        su = _get_builtin('SpatialUI')
        if su is None:
            su, _e = _import_module('SpatialUI')
        caps['spatialui'] = _enum_target(su, _UI_NAMES)

        cam = None
        for cand in ('camera', 'Camera', 'gameplayCamera', 'GameplayCamera'):
            cam = _get_builtin(cand)
            if cam is not None:
                caps['camera_source'] = cand
                break
        if cam is None:
            bw = _get_builtin('BigWorld')
            if bw is None:
                bw, _e2 = _import_module('BigWorld')
            try:
                cam = bw.camera()
                caps['camera_source'] = 'BigWorld.camera()'
            except Exception as e:
                caps['camera_source'] = 'ERR ' + repr(e)[:80]
        caps['camera'] = _enum_target(cam, _CAMERA_NAMES)

        b = _get_builtin('battle')
        caps['battle'] = _enum_target(b, _BATTLE_NAMES)
        try:
            pi = b.getPlayersInfo()
            if isinstance(pi, dict) and len(pi) > 0:
                k = pi.keys()[0]
                caps['player_obj_fields'] = _enum_target(
                    pi[k], _PLAYER_FIELD_NAMES + _OBJ_EXTRA)
                try:
                    si = b.getPlayerShipInfo(k)
                    caps['ship_obj_fields'] = _enum_target(
                        si, _PLAYER_FIELD_NAMES + _OBJ_EXTRA)
                except Exception as e:
                    caps['ship_obj_fields'] = 'ERR ' + repr(e)[:80]
        except Exception as e:
            caps['player_obj_fields'] = 'ERR ' + repr(e)[:80]

        caps['replay'] = _enum_target(_get_builtin('replay'), _REPLAY_NAMES)
        caps['ui'] = _enum_target(_get_builtin('ui'), (
            'showMessage', 'addMessage', 'showHint', 'addHint', 'showNotification',
            'notify', 'setVisible', 'addUiMessage', 'createMarker', 'addMarker',
            'removeMarker', 'showPanel', 'logInfo', 'getWindow', 'callFlash'))
        caps['dataHub'] = _enum_target(_get_builtin('dataHub'), (
            'getEntityClassName', 'getContent', 'getCollectionByDataChannelID',
            'collectionMemberWasAddedExternally', 'getData', 'subscribe'))
        caps['utils'] = _enum_target(_get_builtin('utils'), (
            'getShipName', 'getShipType', 'getShipTier', 'getPlayerName', 'getTeamId',
            'worldToScreen', 'getDistance', 'logInfo', 'logError'))
        caps['debug_text'] = {}
        for src in ('DebugTextDrawer', 'debugTextDrawer', 'PyDebugTextDrawer'):
            o = _get_builtin(src)
            if o is None:
                o, _e3 = _import_module(src)
            if o is not None:
                caps['debug_text'][src] = _enum_target(o, _DEBUG_TEXT_NAMES)

        ev = _get_builtin('events')
        ev_hits = {}
        for name in _EVENT_EXTRA:
            try:
                v = getattr(ev, name)
                ev_hits[name] = 'callable' if callable(v) else 'value'
            except Exception:
                pass
        caps['event_names_present'] = ev_hits

        # ---- v3：坐标 / 可见性问题的最终裁决 ----
        # 把 battle 的 5 个方法都真调一遍，用最大的字段名猜测表 getattr，
        # 只要任何一个返回对象里出现 position / isVisible，坐标问题就有解。
        pos_probe = {}
        big_names = _PLAYER_FIELD_NAMES + _OBJ_EXTRA + _POS_EXTRA
        b2 = _get_builtin('battle')
        try:
            pi2 = b2.getPlayersInfo()
            first_eid = pi2.keys()[0] if isinstance(pi2, dict) and len(pi2) else None
        except Exception:
            pi2, first_eid = None, None
        for fn, arg in (('getSelfPlayerInfo', None),
                        ('getPlayerShipInfo', first_eid),
                        ('getPlayerInfo', first_eid),
                        ('getBattleStatistics', None)):
            try:
                f = getattr(b2, fn)
                r = f() if arg is None else f(arg)
                pos_probe[fn] = _enum_target(r, big_names)
            except Exception as e:
                pos_probe[fn] = 'ERR ' + repr(e)[:100]
        caps['coord_probe'] = pos_probe

        # flash / constants / input：若能调用 flash，就能直接往战斗 HUD 上画东西
        for mn in ('flash', 'constants', 'input', 'devmenu', 'customPorts',
                   'web', 'dock', 'contentSdk'):
            o = _get_builtin(mn)
            if o is None:
                continue
            hit = _enum_target(o, _FLASH_EXTRA + _POS_EXTRA)
            if hit:
                caps['extra_' + mn] = hit
        caps['extra_swept'] = ['flash', 'constants', 'input', 'devmenu',
                               'customPorts', 'web', 'dock', 'contentSdk']

        _write('capabilities.json', caps)
        _caps_done = True
    except Exception as e:
        _write('capabilities.json', {'ts': _now(), 'fatal': repr(e)[:200]})


# ============================================================
#  实时数据采集
# ============================================================
def _collect_players():
    global _players_last
    try:
        now = time.time()
        if now - _players_last < PLAYERS_INTERVAL:
            return
        _players_last = now
        b = _get_builtin('battle')
        if b is None:
            return
        pi = b.getPlayersInfo()
        if not isinstance(pi, dict):
            return
        rows = []
        for eid, o in pi.items():
            if not isinstance(eid, (int, long)) or o is None:
                continue
            key = str(eid)
            row = {'entityId': eid,
                   'name': _safe_get(o, 'name'),
                   'teamId': _safe_get(o, 'teamId'),
                   'isBot': _safe_get(o, 'isBot'),
                   'isOwn': _safe_get(o, 'isOwn'),
                   'alive': _safe_get(o, 'isAlive'),
                   'maxHealth': _safe_get(o, 'maxHealth'),
                   'arenaShipId': _safe_get(o, 'shipId')}
            info = _shipinfo_cache.get(key)
            if info is None:
                info = {}
                try:
                    si = b.getPlayerShipInfo(eid)
                    if si is not None:
                        info = {'shipGlobalId': _safe_get(si, 'id'),
                                'shipInternal': _safe_get(si, 'name'),
                                'tier': _safe_get(si, 'level'),
                                'shipMaxHealth': _safe_get(si, 'maxHealth')}
                except Exception:
                    pass
                _shipinfo_cache[key] = info
            for k, v in info.items():
                row[k] = v
            rows.append(row)
        _write('players.json', {'ts': _now(), 'count': len(rows), 'players': rows})
        _update_live_state(rows)
    except Exception:
        pass


def _update_live_state(rows):
    global _my_eid, _my_team, _my_arena
    try:
        for r in rows:
            try:
                _team_of[int(r['entityId'])] = r.get('teamId')
            except Exception:
                pass
            try:
                aid = int(r['arenaShipId'])
                _arena_team_of[aid] = r.get('teamId')
            except Exception:
                pass
            if r.get('isOwn'):
                try:
                    _my_eid = int(r['entityId'])
                except Exception:
                    pass
                try:
                    _my_arena = int(r['arenaShipId'])
                except Exception:
                    pass
                _my_team = r.get('teamId')
        team_alive = enemy_alive = team_hp = enemy_hp = 0
        my_hp = my_max = None
        my_ship = ''
        my_tier = None
        team_n = enemy_n = 0
        for r in rows:
            t = r.get('teamId')
            hp = r.get('maxHealth') or 0
            if t == _my_team:
                team_n += 1
                if r.get('alive'):
                    team_alive += 1
                    team_hp += hp
                if r.get('isOwn'):
                    my_hp = r.get('maxHealth')
                    my_max = r.get('maxHealth')
                    my_ship = r.get('shipInternal') or ''
                    my_tier = r.get('tier')
            else:
                enemy_n += 1
                if r.get('alive'):
                    enemy_alive += 1
                    enemy_hp += hp
        _write('state.json', {
            'ts': _now(), 'event': 'live', 'active': _battle_active,
            'my_health': my_hp, 'my_max_health': my_max,
            'my_ship_internal': my_ship, 'my_tier': my_tier,
            'teamHP': team_hp, 'teamAlive': team_alive, 'teamCount': team_n,
            'enemyHP': enemy_hp, 'enemyAlive': enemy_alive, 'enemyCount': enemy_n,
            'damageDealt': _live['damageDealt'], 'damageReceived': _live['damageReceived'],
            'frags': _live['frags'], 'shots': _live['shots'], 'hits': _live['hits'],
            'fires': _live['fires'], 'floods': _live['floods'],
            'citadels': _live['citadels']})
    except Exception:
        pass


# ============================================================
#  游戏内 HUD（SpatialUI / 屏幕投影 / 文字）
# ============================================================
def _hud_discover():
    """模式 1：只枚举 SpatialUI 成员 + 记录，不创建任何图元（零风险）。"""
    try:
        su = _get_builtin('SpatialUI')
        err = ''
        if su is None:
            su, err = _import_module('SpatialUI')
        h = {'ts': _now(), 'mode': _hud['mode'], 'spatialui': 'None',
             'import_err': err, 'members': {}, 'dir': [], 'types': {}}
        if su is not None:
            h['spatialui'] = type(su).__name__
            h['members'] = _enum_target(su, _UI_NAMES)
            try:
                h['dir'] = sorted([n for n in dir(su) if not n.startswith('_')])[:80]
            except Exception as e:
                h['dir'] = 'ERR ' + repr(e)[:60]
            h['types'] = {}
            for tn in ('Params', 'Rect', 'Box', 'Lines', 'LineStrip', 'Triangles',
                       'Ellipse', 'EllipseContour', 'Regular', 'CurvedQuad',
                       'SingleMeshFigure', 'System', 'LineStripBuilder'):
                try:
                    t = getattr(su, tn)
                except Exception:
                    continue
                try:
                    h['types'][tn] = sorted([n for n in dir(t) if not n.startswith('_')])[:40]
                except Exception:
                    h['types'][tn] = 'dir() empty'
        _hud['members'] = h.get('members') or {}
        _write('hud_state.json', h)
        return True
    except Exception as e:
        _write('hud_state.json', {'ts': _now(), 'fatal': repr(e)[:200]})
        return False


def _hud_draw_probe():
    """模式 2：尝试创建图元并把结果逐个记录（可能让游戏报错，请先在训练房试）。"""
    res = {'ts': _now(), 'mode': 2, 'attempts': []}
    su = _get_builtin('SpatialUI')
    if su is None:
        su, err = _import_module('SpatialUI')
        if su is None:
            res['fatal'] = 'SpatialUI 不可用: ' + err
            _write('hud_state.json', res)
            return False

    def try_call(label, fn, *a, **kw):
        try:
            r = fn(*a, **kw)
            res['attempts'].append({'what': label, 'ok': True,
                                    'ret': type(r).__name__ if r is not None else 'None'})
            return r
        except Exception as e:
            res['attempts'].append({'what': label, 'ok': False, 'err': repr(e)[:140]})
            return None

    cam = None
    for cand in ('camera', 'GameplayCamera', 'gameplayCamera'):
        cam = _get_builtin(cand)
        if cam is not None:
            break
    if cam is None:
        bw = _get_builtin('BigWorld')
        if bw is None:
            bw, _e = _import_module('BigWorld')
        try:
            cam = bw.camera()
        except Exception as e:
            res['camera'] = 'ERR ' + repr(e)[:80]
    res['camera'] = type(cam).__name__ if cam is not None else 'None'
    if cam is not None:
        res['aspect'] = try_call('camera.getAspectRatio', cam.getAspectRatio)

    made = {}
    for tn in ('Rect', 'Box', 'Lines', 'LineStrip', 'Triangles', 'Ellipse',
               'EllipseContour', 'Regular', 'CurvedQuad', 'SingleMeshFigure'):
        t = None
        try:
            t = getattr(su, tn)
        except Exception:
            continue
        for argtuple, tag in (((), 'noargs'), ((1.0, 1.0), '2f'),
                              (((0, 0, 0), (1, 1, 0)), '2v3')):
            o = try_call('SpatialUI.%s/%s' % (tn, tag), t, *argtuple)
            if o is not None:
                made[tn] = o
                break

    for tn, o in made.items():
        try_call('visible(%s,True)' % tn, su.visible, o, True)
        try_call('lineWidth(%s,2)' % tn, su.lineWidth, o, 2.0)
        try_call('colorFactor(%s)' % tn, su.colorFactor, o, (1.0, 0.3, 0.3, 1.0))
        _hud['draws'] += 1

    res['created'] = made.keys()
    res['draws'] = _hud['draws']
    _hud['ok'] = len(made) > 0
    _hud['attempts'] = res['attempts']
    _write('hud_state.json', res)
    return _hud['ok']


# ============================================================
#  事件处理
# ============================================================
def _on_battle_start(*args):
    global _battle_active, _caps_at, _caps_done, _caps_phase, _players_last
    global _team_of, _arena_team_of, _my_eid, _my_arena
    try:
        _ensure_connected()
        _battle_active = True
        _write('battle.json', {'ts': _now(), 'event': 'battle_start'})
        _write('state.json', {'ts': _now(), 'event': 'battle_start', 'active': True})
        _clear_file('battle_end.json')
        _clear_file('stats_parsed.json')
        _clear_file('stats_full.json')
        _shipinfo_cache.clear()
        # 每局 id 都会重新分配，不清会串局（上一局的 arenaShipId 撞上这一局）
        _team_of.clear()
        _arena_team_of.clear()
        _my_eid = None
        _my_arena = None
        _players_last = 0.0
        _caps_at = time.time() + CAPS_DELAY
        _caps_done = False
        _caps_phase = 0
        for k in _live:
            _live[k] = 0
        _event({'ts': _now(), 'type': 'battle_start'})
    except Exception:
        pass


def _on_battle_end(*args):
    global _battle_active
    try:
        _battle_active = False
        _write('battle_end.json', {'ts': _now(), 'event': 'battle_end'})
        _write('state.json', {'ts': _now(), 'event': 'battle_end', 'active': False})
        _event({'ts': _now(), 'type': 'battle_end'})
    except Exception:
        pass


_STATS_KEYS = ('name', 'clan_tag', 'clan_id', 'home_realm', 'damage', 'damage_sum',
               'damage_main', 'damage_main_ap', 'damage_main_he', 'damage_fire',
               'damage_flood', 'damage_tpd_deep', 'damage_etc', 'ships_killed',
               'first_ships_spotted', 'team_ships_killed', 'shots_main_ap', 'shots_main_he',
               'shots_tpd', 'hits_main', 'hits_main_ap', 'hits_main_he', 'pierced_hits_main',
               'citadels', 'hits_fire', 'hits_flood', 'module_fires', 'module_breaks',
               'module_crits', 'received_damage_sum', 'received_damage_by_ap',
               'received_damage_by_he', 'received_hits_by_artillery', 'max_health',
               'remained_hp', 'is_alive', 'life_time_sec', 'exp', 'raw_exp', 'team_id',
               'vehicle_type_id', 'killer_db_id', 'killer_veh_id', 'killer_weapon',
               'distance', 'agro_total', 'agro_art', 'agro_air', 'agro_tpd',
               'tpds_spotted', 'capture_points', 'dropped_capture_points',
               'team_captured_points', 'team_dropped_points', 'achievements')


def _on_stats(*args):
    """onBattleStatsReceived：stats_parsed.json（摘要）+ stats_full.json（完整）。"""
    try:
        stats = None
        for a in args:
            if isinstance(a, dict):
                stats = a
                break
        if stats is None:
            return
        d = {'ts': _now(), 'event': 'stats_parsed'}
        me = stats.get('me')
        if isinstance(me, dict):
            for k in _STATS_KEYS:
                if k in me:
                    d[k] = me[k]
            mins = me.get('interactions')
            if isinstance(mins, dict) and len(mins) > 0:
                rows = []
                for pid, info in mins.items():
                    if not isinstance(info, dict):
                        continue
                    rows.append({'playerId': pid,
                                 'damage_all': info.get('damage_all'),
                                 'damage_main_he': info.get('damage_main_he'),
                                 'damage_main_ap': info.get('damage_main_ap'),
                                 'damage_fire': info.get('damage_fire'),
                                 'hits_main': info.get('hits_main'),
                                 'citadels': info.get('citadels'),
                                 'floods': info.get('floods')})
                if rows:
                    d['enemy_damage'] = rows
        com = stats.get('common')
        if isinstance(com, dict):
            for k in ('battle_type', 'game_mode', 'duration_sec', 'winner_team_id',
                      'win_type_id', 'map_type_id', 'scenario_name', 'arena_id', 'start_dt'):
                if k in com:
                    d[k] = com[k]
        _write('stats_parsed.json', d)

        full = {'ts': _now(), 'common': com if isinstance(com, dict) else None}
        inter = stats.get('interactions')
        if isinstance(inter, dict):
            slim = {}
            for cat, arr in inter.items():
                if not isinstance(arr, list):
                    continue
                keep = []
                for it in arr:
                    if not isinstance(it, dict) or not it:
                        continue
                    row = {}
                    for k in ('playerId', 'shipId', 'is_team_ally', 'damage_all',
                              'damage_main', 'damage_main_ap', 'damage_main_he',
                              'damage_main_cs', 'damage_fire', 'damage_flood', 'damage_ram',
                              'hits_main', 'hits_main_ap', 'hits_main_he', 'hits_main_cs',
                              'hits_fire', 'hits_flood', 'hits_tpd', 'hits_ram',
                              'citadels', 'fires', 'floods',
                              'module_crits', 'module_breaks',
                              'module_crits_engine', 'module_crits_steering_gear',
                              'module_crits_torpedo_tube', 'module_breaks_artillery',
                              'module_breaks_torpedo_tube', 'module_breaks_air_defense'):
                        if k in it:
                            row[k] = it[k]
                    if row:
                        keep.append(row)
                if keep:
                    slim[cat] = keep
            full['interactions'] = slim
        pd = stats.get('privateData')
        if isinstance(pd, dict):
            econ = {}
            for k in ('credits', 'credits_profit', 'exp', 'exp_profit', 'base_exp',
                      'free_exp', 'total_credits', 'is_premium', 'premium_type'):
                if k in pd:
                    econ[k] = pd[k]
            full['private'] = econ
        _write('stats_full.json', full)
        _event({'type': 'stats_parsed', 'ts': _now()})
    except Exception:
        pass


def _on_ribbon(*args):
    try:
        kind = None
        count = None
        for a in args:
            if isinstance(a, (int, long)):
                if kind is None:
                    kind = int(a)
                elif count is None:
                    count = int(a)
        name = _RIBBON_NAMES.get(kind, '未知缎带(%s)' % (kind,))
        c = count or 1
        if kind == 5:
            _live['frags'] += c
        elif kind == 8:
            _live['citadels'] += c
        elif kind == 6:
            _live['fires'] += c
        elif kind == 7:
            _live['floods'] += c
        _event({'ts': _now(), 'type': 'ribbon', 'kind': kind, 'name': name, 'count': count})
    except Exception:
        pass


def _on_shell(*args):
    try:
        vals = list(args)
        d = {'ts': _now(), 'type': 'shell'}
        d['argc'] = len(vals)   # 记录回调实际给了几个参数：>7 说明还有没解析的字段（可能有坐标）
        if len(vals) >= 6:
            d['victimId'] = vals[0]
            d['shooterId'] = vals[1]
            d['ammoId'] = vals[2]
            d['matId'] = vals[3]
            d['shotId'] = vals[4]
            try:
                fi = int(vals[5])
                d['flags'] = [n for bit, n in _SHELL_FLAGS if fi & bit]
            except Exception:
                d['flags'] = []
            if len(vals) >= 7:
                d['damage'] = vals[6]
                try:
                    dmg = int(vals[6])
                    shooter = int(vals[1])
                    victim = int(vals[0])
                    st = _arena_team_of.get(shooter)
                    vt = _arena_team_of.get(victim)
                    if dmg > 0 and st is not None and vt is not None and st != vt:
                        _live['hits'] += 1
                        if _my_arena is not None and shooter == _my_arena:
                            _live['damageDealt'] += dmg
                        elif _my_arena is not None and victim == _my_arena:
                            _live['damageReceived'] += dmg
                except Exception:
                    pass
        _event(d)
    except Exception:
        pass


def _on_achievement(*args):
    try:
        _event({'ts': _now(), 'type': 'achievement',
                'args': [repr(a)[:80] for a in args]})
    except Exception:
        pass


def _on_sfm(evName, evData):
    """onSFMEvent：UI 窗口事件（进/出战斗的可靠信号），同时全量记录。"""
    try:
        nm = ''
        try:
            if isinstance(evData, dict):
                nm = evData.get('windowName', '') or ''
        except Exception:
            pass
        if evName == 'window.show' and nm == 'Battle':
            _on_battle_start()
        elif evName == 'window.hide' and nm == 'Battle':
            _on_battle_end()
        _event({'ts': _now(), 'type': 'sfm', 'name': evName, 'window': nm})
    except Exception:
        pass


# ============================================================
#  看门狗（callbacks.perTick 每 tick 驱动）
# ============================================================
def _watchdog_tick():
    global _connected, _last_heartbeat, _last_retry, _caps_phase
    try:
        now = time.time()
        if not _connected:
            if now - _last_retry >= RETRY_INTERVAL:
                _last_retry = now
                _try_connect()
        else:
            if now - _last_heartbeat >= HEARTBEAT_INTERVAL:
                _last_heartbeat = now
                _write_ready('connected')
            if _battle_active:
                _collect_players()
        if _battle_active and (not _caps_done) and _caps_at > 0 and now >= _caps_at:
            if _caps_phase == 0:
                _caps_phase = 1
                try:
                    if HUD_MODE == 2:
                        _hud_draw_probe()
                    elif HUD_MODE == 1:
                        _hud_discover()
                except Exception:
                    pass
            elif _caps_phase == 1:
                _caps_phase = 2
                _run_capabilities()
    except Exception:
        pass


# ============================================================
#  常量表
# ============================================================
_RIBBON_NAMES = {
    0: '主炮命中', 1: '鱼雷命中', 2: '炸弹命中', 3: '击落飞机', 4: '部件损毁',
    5: '击毁', 6: '点火', 7: '进水', 8: '核心区', 9: '占点防御',
    10: '占领', 11: '协助占领', 12: '压制', 13: '副炮命中', 14: '过穿',
    15: '穿透', 16: '未击穿', 17: '跳弹', 18: '击毁建筑', 19: '点亮',
    20: '炸弹过穿', 21: '俯冲炸弹穿透', 22: '炸弹未击穿', 23: '炸弹跳弹',
    24: '火箭命中', 25: '火箭穿透', 26: '火箭未击穿', 27: '防空击落飞机',
    28: '鱼雷防护命中', 29: '炸弹鱼雷防护命中', 30: '火箭鱼雷防护命中',
    31: '深弹命中', 32: '声呐命中', 33: '投放', 34: '火箭跳弹',
    35: '火箭过穿', 36: '波攻击毁鱼雷', 37: '切波', 38: '波命中舰船',
    39: '声学命中(新目标)', 40: '声学命中(当前目标)', 41: '声学命中(阻挡)',
    42: '酸性伤害', 43: '深弹全额伤害', 44: '深弹部分伤害', 45: '水雷命中',
    46: '排雷', 47: '清除雷区', 48: '光子鱼雷命中', 49: '光子鱼雷溅射',
    50: '光子鱼雷瞄准脉冲', 51: '相位激光', 52: '护盾命中', 53: '护盾移除',
    54: '协助伤害', 55: '导弹命中', 56: '击落导弹', 57: '波',
    58: '光子鱼雷', 59: '护盾',
}

_SHELL_FLAGS = ((1, '我方受伤'), (2, '穿透'), (4, '水下'), (8, '被毁'), (16, '过穿'),
                (32, '跳弹'), (64, '溅射'), (128, '主炮被毁'), (256, '鱼雷管被毁'),
                (512, '副炮被毁'))

_EVENT_CANDIDATES = (
    ('onBattleStarted', _on_battle_start),
    ('onBattleStart', _on_battle_start),
    ('onBattleEnd', _on_battle_end),
    ('onBattleQuit', _on_battle_end),
    ('onBattleStatsReceived', _on_stats),
    ('onGotRibbon', _on_ribbon),
    ('onReceiveShellInfo', _on_shell),
    ('onAchievementEarned', _on_achievement),
    ('onSFMEvent', _on_sfm),
)

# ---------- 入口 ----------
_try_connect()
print '[WowsBAMod v2] loaded (status=%s, subscribed=%d, hud_mode=%d)' % ('connected' if _connected else 'connecting', _subscribed_ok, HUD_MODE)
