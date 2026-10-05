using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 《战舰世界》官方 ModsAPI 模组安装器。
/// 只写文件到游戏官方认可的自定义模组目录 res_mods\&lt;版本&gt;\PnFMods\，
/// 不修改游戏任何原始文件、不注入进程、不提供游戏内优势，属官方
/// 《模组和第三方软件使用政策》允许的创造性模组范畴。
/// </summary>
public static class WowsModInstaller
{
    public const string ModName = "WowsBAMod";
    private const string LoaderFileName = "PnFModsLoader.py";

    /// <summary>
    /// 在 gamePath\bin\* 下定位最新版本目录的 res_mods 路径。
    /// 找不到返回 null。
    /// </summary>
    public static string? ResolveResModsDir(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;
        var binDir = Path.Combine(gamePath, "bin");
        if (!Directory.Exists(binDir)) return null;

        var versions = Directory.GetDirectories(binDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && n.Length >= 6 && n.All(char.IsDigit))
            .OrderByDescending(n => n)
            .ToList();
        if (versions.Count == 0) return null;

        return Path.Combine(binDir, versions[0]!, "res_mods");
    }

    /// <summary>当前游戏 bin 版本号（如 13243917），找不到返回空。</summary>
    public static string ResolveVersionLabel(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return "";
        var binDir = Path.Combine(gamePath, "bin");
        if (!Directory.Exists(binDir)) return "";
        return Directory.GetDirectories(binDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && n.Length >= 6 && n.All(char.IsDigit))
            .OrderByDescending(n => n)
            .FirstOrDefault() ?? "";
    }

    /// <summary>旧版模组数据目录（%APPDATA%\WoWSBattleAssistant\mods_data，历史兼容保留）。</summary>
    public static string GetModDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WoWSBattleAssistant", "mods_data");

    /// <summary>
    /// 运行时模组数据目录：游戏内 mod 实际写入的位置
    /// （res_mods\&lt;版本&gt;\PnFMods\WowsBAMod\data，沙箱禁 os/json 后以 __file__ 推导）。
    /// 找不到游戏 bin 时返回 null。
    /// </summary>
    public static string? GetRuntimeModDataDir(string? gamePath)
    {
        var resMods = ResolveResModsDir(gamePath);
        if (resMods == null) return null;
        return Path.Combine(resMods, "PnFMods", ModName, "data");
    }

    /// <summary>模组是否已安装（激活文件与 Main.py 都存在）。</summary>
    public static bool IsInstalled(string? gamePath)
    {
        var resMods = ResolveResModsDir(gamePath);
        if (resMods == null) return false;
        return File.Exists(Path.Combine(resMods, LoaderFileName)) &&
               File.Exists(Path.Combine(resMods, "PnFMods", ModName, "Main.py"));
    }

    /// <summary>
    /// 安装模组：创建 PnFModsLoader.py（ModsAPI 激活文件）+ PnFMods\WowsBAMod\Main.py + README，
    /// 并确保数据目录存在。只新增文件，不触碰用户已有模组与游戏原始文件。
    /// </summary>
    public static (bool Ok, string Message) Install(string? gamePath)
    {
        try
        {
            var resMods = ResolveResModsDir(gamePath);
            if (resMods == null)
                return (false, "未找到游戏 bin 目录，请先在设置中填写并验证《战舰世界》安装目录。");

            // 1) ModsAPI 激活文件（官方机制：该文件存在即激活 ModsAPI）
            var loader = Path.Combine(resMods, LoaderFileName);
            if (!File.Exists(loader))
                File.WriteAllText(loader,
                    "# WoWSBattleAssistant ModsAPI activation (official ModsAPI loader)\n",
                    new UTF8Encoding(false));

            // 2) 模组本体
            var modDir = Path.Combine(resMods, "PnFMods", ModName);
            Directory.CreateDirectory(modDir);
            var mainPy = Path.Combine(modDir, "Main.py");
            File.WriteAllText(mainPy, LoadMainPyTemplate(resMods), new UTF8Encoding(false));

            // 3) 说明文件
            File.WriteAllText(Path.Combine(modDir, "README.txt"),
                "WowsBAMod - WoWSBattleAssistant 对局数据采集模组\n" +
                "基于官方 ModsAPI (API_v1.0)，只读取游戏状态并输出到\n" +
                "%APPDATA%\\WoWSBattleAssistant\\mods_data\\，供外部助手离线分析。\n" +
                "不修改游戏行为、不提供游戏内优势。\n",
                new UTF8Encoding(false));

            // 4) 数据目录（mod 写入 game res_mods\PnFMods\WowsBAMod\data，助手从此读取）
            Directory.CreateDirectory(GetModDataDir());
            try { Directory.CreateDirectory(Path.Combine(modDir, "data")); } catch { }

            return (true, $"✅ 模组已安装：{modDir}\n数据目录：{Path.Combine(modDir, "data")}\n下次启动游戏生效。");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"模组安装失败: {ex.Message}");
            return (false, "❌ 安装失败：" + ex.Message);
        }
    }

    /// <summary>卸载本模组（只删自己的文件；PnFMods 目录空时一并删除，不影响用户其他模组）。</summary>
    public static (bool Ok, string Message) Uninstall(string? gamePath)
    {
        try
        {
            var resMods = ResolveResModsDir(gamePath);
            if (resMods == null)
                return (false, "未找到游戏 bin 目录。");

            var modDir = Path.Combine(resMods, "PnFMods", ModName);
            if (Directory.Exists(modDir))
            {
                try { Directory.Delete(modDir, recursive: true); }
                catch (Exception ex) { return (false, "❌ 删除模组目录失败：" + ex.Message + "（可能被游戏占用，请先退出游戏）"); }
            }

            var loader = Path.Combine(resMods, LoaderFileName);
            if (File.Exists(loader))
            {
                try { File.Delete(loader); }
                catch (Exception ex) { return (false, "❌ 删除激活文件失败：" + ex.Message); }
            }

            // PnFMods 目录若空则清理
            var pnfDir = Path.Combine(resMods, "PnFMods");
            if (Directory.Exists(pnfDir) && !Directory.EnumerateFileSystemEntries(pnfDir).Any())
            {
                try { Directory.Delete(pnfDir); } catch { /* 非关键 */ }
            }

            return (true, "✅ 模组已卸载。数据文件保留在 " + GetModDataDir() + "（可手动清理）。");
        }
        catch (Exception ex)
        {
            return (false, "❌ 卸载失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 读取 Main.py 模板。降级链：
    /// 1) 程序集内嵌资源（首选，随版本发布）；2) 游戏 res_mods 里已安装的旧副本
    /// （嵌入式资源意外缺失时复用）；3) 内置精简兜底（连接+重试+心跳核心）。
    /// 保证安装永远可用。
    /// </summary>
    private static string LoadMainPyTemplate(string? resModsDir)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("GameMods.WowsBAMod.Main.py", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name)!;
                using var r = new StreamReader(s);
                var text = r.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取模组模板失败，使用兜底: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(resModsDir))
        {
            try
            {
                var installed = Path.Combine(resModsDir, "PnFMods", ModName, "Main.py");
                if (File.Exists(installed))
                {
                    var text = File.ReadAllText(installed, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取已安装模组副本失败: {ex.Message}");
            }
        }
        return MainPyFallback;
    }

    /// <summary>内置兜底模板（精简版：连接+自动重试+心跳+基础事件采集）。</summary>
    private const string MainPyFallback = """
# -*- coding: utf-8 -*-
# WowsBAMod - WoWSBattleAssistant battle data collector (official ModsAPI v1)
# Sandbox whitelist: xml, copy, struct, Keys, Math, datetime, re, math, time, array, SpatialUI, xml.dom
# -> no os/json/sys allowed. Paths derive from __file__; JSON is hand-rolled.
# Data written to <mod_dir>\data\ for the external assistant to read.
import time

API_VERSION = 'API_v1.0'
MOD_NAME = 'WowsBAMod'
RETRY_INTERVAL = 8
HEARTBEAT_INTERVAL = 30

# ---------- paths (no os module) ----------
try:
    _mf = __file__
    if '\\' in _mf:
        _mod_dir = _mf.rsplit('\\', 1)[0]
    else:
        _mod_dir = _mf.rsplit('/', 1)[0]
except Exception:
    _mod_dir = ''
_data_dir = _mod_dir + '\\data' if _mod_dir else 'data'

# ---------- minimal JSON writer (no json module) ----------
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
        return '%d' % v
    if t is float:
        if v != v or v == float('inf') or v == float('-inf'):
            return 'null'
        return repr(v)
    if t is unicode:
        v = v.encode('utf-8')
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
        return '[' + ','.join(_js(x) for x in v) + ']'
    if t is dict:
        parts = []
        for k, val in v.items():
            parts.append(_js(str(k)) + ':' + _js(val))
        return '{' + ','.join(parts) + '}'
    return _js(str(v))

def _write(name, obj):
    try:
        f = open(_data_dir + '\\' + name, 'w')
        f.write(_js(obj))
        f.close()
    except Exception:
        pass

# 事件缓冲：沙箱拦截 open('a') 追加模式（日志报 "Path 'data/events.jsonl' does not exists"），
# 因此改为内存缓冲 + 'w' 整写（'w' 模式已验证可用）。
_events_buffer = []

def _event(obj):
    global _events_buffer
    try:
        _events_buffer.append(obj)
        if len(_events_buffer) > 100:
            _events_buffer = _events_buffer[-100:]
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

def _clear_file(name):
    # 沙箱无 os.remove：用 open('w') 覆盖为空，助手读到空后清除该文件缓存
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

# ---------- connection state ----------
_connected = False
_subscribed_ok = 0
_subscribed_names = set()
_last_heartbeat = 0.0
_battle_active = False
_diagnosed = False

# ---------- subscribe with diagnostics ----------
# ModsAPI 沙箱把 API 注入 builtins（events/callbacks/battle/dataHub/utils/constants/
# input/ui/web/flash/dock/replay/contentSdk/customPorts/devmenu），无需 import。
def _get_builtin(name):
    bi = __builtins__
    if isinstance(bi, dict):
        return bi.get(name)
    return getattr(bi, name, None)

def _subscribe():
    global _diagnosed
    ev = _get_builtin('events')
    diag = {'subscribe': {}, 'api_members': {}}
    diag['subscribe']['events_obj'] = ('OK type=' + type(ev).__name__) if ev is not None else 'None'
    if ev is not None:
        try:
            diag['subscribe']['events_members'] = sorted([n for n in dir(ev) if not n.startswith('_')])
        except Exception as e:
            diag['subscribe']['events_members'] = 'ERR ' + repr(e)
    ok = 0
    for name, cb in _EVENT_CANDIDATES:
        if name in _subscribed_names:
            ok += 1
            continue
        try:
            fn = getattr(ev, name, None) if ev is not None else None
            diag['subscribe'][name] = 'fn=' + repr(fn)[:80] if fn is not None else 'NO ATTR'
            if fn is not None:
                fn(cb)
                _subscribed_names.add(name)
                ok += 1
        except Exception as e:
            diag['subscribe'][name] = 'ERR ' + repr(e)
    # 看门狗：callbacks.perTick 每 tick 驱动（沙箱禁 threading，无需 import）
    try:
        cb_api = _get_builtin('callbacks')
        if cb_api is not None and hasattr(cb_api, 'perTick'):
            cb_api.perTick(_watchdog_tick)
            diag['subscribe']['watchdog'] = 'perTick OK'
        else:
            diag['subscribe']['watchdog'] = 'callbacks None or no perTick: ' + repr(cb_api)[:100]
    except Exception as e:
        diag['subscribe']['watchdog'] = 'ERR ' + repr(e)
    # 注入 API 成员清单（一次性诊断）
    if not _diagnosed:
        _diagnosed = True
        for g in ('battle', 'dataHub', 'utils', 'constants', 'input', 'ui', 'web',
                  'flash', 'dock', 'replay', 'contentSdk', 'customPorts', 'devmenu', 'callbacks'):
            try:
                o = _get_builtin(g)
                if o is not None:
                    diag['api_members'][g] = sorted([n for n in dir(o) if not n.startswith('_')])[:40]
                else:
                    diag['api_members'][g] = 'None'
            except Exception as e:
                diag['api_members'][g] = 'ERR ' + repr(e)
        _write('diag.json', diag)
    return ok

def _write_ready(status):
    _write('mod_ready.json', {
        'ts': _now(), 'api': API_VERSION, 'status': status,
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

_last_retry = 0.0

def _watchdog_tick():
    # 每 tick 调用：未连接 → 限频重试；已连接 → 限频心跳
    global _connected, _last_heartbeat, _last_retry
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
    except Exception:
        pass

# ---------- ribbon kind -> 中文名 ----------
# 来源：wows-core game_types::Ribbon::from_id（RibbonsType，build 12506899），
# 与 onGotRibbon 事件 kind 数值一一对应（modern id space 0..=59）。
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

# ---------- shell 事件 booleans 位掩码（ModsAPI 官方文档）----------
# 1=我方舰船受伤 2=装甲穿透 4=水下损伤 8=舰船被毁 16=炮弹穿过
# 32=跳弹 64=溅射 128=主炮被毁 256=鱼雷管被毁 512=副炮被毁
_SHELL_FLAGS = (
    (1, '我方受伤'), (2, '穿透'), (4, '水下'), (8, '被毁'), (16, '过穿'),
    (32, '跳弹'), (64, '溅射'), (128, '主炮被毁'), (256, '鱼雷管被毁'), (512, '副炮被毁'),
)

# ---------- stats（战后）me 关键字段 ----------
_STATS_KEYS = (
    'name', 'damage', 'damage_sum', 'damage_main', 'damage_main_ap', 'damage_main_he',
    'damage_fire', 'damage_flood', 'damage_atba_he', 'damage_tpd_deep', 'damage_etc',
    'ships_killed', 'first_ships_spotted', 'team_ships_killed',
    'shots_main_ap', 'shots_main_he', 'shots_tpd',
    'hits_main', 'hits_main_ap', 'hits_main_he', 'pierced_hits_main', 'citadels',
    'hits_fire', 'hits_flood', 'module_fires', 'module_breaks', 'module_crits',
    'received_damage_sum', 'received_damage_by_ap', 'received_damage_by_he',
    'received_hits_by_artillery', 'received_hits_main_ap', 'received_hits_by_he',
    'max_health', 'remained_hp', 'is_alive', 'life_time_sec',
    'exp', 'raw_exp', 'team_id', 'team_captured_points', 'team_dropped_points',
    'cp_capture_points', 'cp_dropped_points',
)

# ---------- event handlers ----------
def _on_battle_start(*args):
    global _battle_active
    _ensure_connected()
    _battle_active = True
    _write('battle.json', {'ts': _now(), 'event': 'battle_start'})
    # 关键：对局开始立即标记 state active=True，并清空上一局残留的 battle_end.json，
    # 否则助手会误判"对局结束"（state 仍是上局 battle_end、battle_end.json 残留）。
    _write('state.json', {'ts': _now(), 'event': 'battle_start', 'active': True})
    _clear_file('battle_end.json')
    _event({'ts': _now(), 'type': 'battle_start'})

def _on_battle_end(*args):
    global _battle_active
    _battle_active = False
    _write('battle_end.json', {'ts': _now(), 'event': 'battle_end'})
    _write('state.json', {'ts': _now(), 'event': 'battle_end', 'active': False})
    _event({'ts': _now(), 'type': 'battle_end'})

def _on_stats(*args):
    # onBattleStatsReceived：战后完整统计。参数[0] 为 stats dict：
    #   me           —— 自己完整统计（含 interactions 对敌伤害明细）
    #   common       —— 对局信息（模式/时长/胜负/地图）
    #   interactions —— 按类型分组（Artillery/Torpedo/...）的交互明细
    # 提取关键字段写 stats_parsed.json（供复盘/AI），state.json 仍管实时状态。
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
            # 对敌伤害明细（me.interactions: playerId -> dict）
            mins = me.get('interactions')
            if isinstance(mins, dict) and len(mins) > 0:
                rows = []
                for pid, info in mins.items():
                    if not isinstance(info, dict):
                        continue
                    row = {'playerId': pid, 'damage_all': info.get('damage_all'),
                           'damage_main_he': info.get('damage_main_he'),
                           'damage_main_ap': info.get('damage_main_ap'),
                           'damage_fire': info.get('damage_fire'),
                           'hits_main': info.get('hits_main'),
                           'citadels': info.get('citadels'), 'floods': info.get('floods')}
                    rows.append(row)
                if len(rows) > 0:
                    d['enemy_damage'] = rows
        com = stats.get('common')
        if isinstance(com, dict):
            for k in ('battle_type', 'game_mode', 'duration_sec', 'winner_team_id',
                      'win_type_id', 'map_type_id', 'scenario_name'):
                if k in com:
                    d[k] = com[k]
        _write('stats_parsed.json', d)
        _event({'type': 'stats_parsed', 'ts': _now()})
    except Exception:
        pass

def _on_ribbon(*args):
    # onGotRibbon：第一个参数 = 缎带 kind 常量值，第二个 = 数量
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
        _event({'ts': _now(), 'type': 'ribbon', 'kind': kind, 'name': name, 'count': count})
    except Exception:
        pass

def _on_shell(*args):
    # onReceiveShellInfo（ModsAPI 官方文档参数顺序）：
    # victimID, shooterID, ammoId, matID, shotID, booleans(位掩码), damage,
    # shotPosition(坐标), yaw(角度), hlinfo(齐射信息)
    try:
        vals = list(args)
        d = {'ts': _now(), 'type': 'shell'}
        if len(vals) >= 6:
            d['victimId'] = vals[0]
            d['shooterId'] = vals[1]
            d['ammoId'] = vals[2]
            d['matId'] = vals[3]
            d['shotId'] = vals[4]
            try:
                fi = int(vals[5])
                d['flags'] = [name for bit, name in _SHELL_FLAGS if fi & bit]
            except Exception:
                d['flags'] = []
            if len(vals) >= 7:
                d['damage'] = vals[6]
        _event(d)
    except Exception:
        pass

def _on_achievement(*args):
    try:
        d = {'ts': _now(), 'type': 'achievement', 'args': [repr(a) for a in args]}
        _event(d)
    except Exception:
        pass

# ---------- event candidates (defined after handlers, evaluated at subscribe time) ----------
# 官方文档事件名: onBattleStarted / onBattleQuit / onReceiveShellInfo / onFlashReady / onSFMEvent
# 下方列表含 360 国服可能的变体名，逐个尝试（getattr 不到自动跳过）。
_EVENT_CANDIDATES = (
    ('onBattleStarted', _on_battle_start),
    ('onBattleStart', _on_battle_start),
    ('onBattleEnd', _on_battle_end),
    ('onBattleQuit', _on_battle_end),
    ('onBattleStatsReceived', _on_stats),
    ('onGotRibbon', _on_ribbon),
    ('onReceiveShellInfo', _on_shell),
    ('onAchievementEarned', _on_achievement),
)

# ---------- entry ----------
# 连接在 _subscribe 中建立；重试/心跳由 callbacks.perTick 每 tick 驱动（无需线程）
_try_connect()
print '[WowsBAMod] loaded (status=%s, subscribed=%d)' % ('connected' if _connected else 'connecting', _subscribed_ok)
""";
}
