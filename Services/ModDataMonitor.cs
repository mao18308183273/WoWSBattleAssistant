using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 官方 ModsAPI 模组（WowsBAMod）输出数据的读取器。
/// 只读 %APPDATA%\WoWSBattleAssistant\mods_data\ 下的 JSON 文件：
///   battle.json        对局开始信息（覆盖写）
///   state.json         实时状态快照（覆盖写，最近一次统计事件）
///   battle_end.json    对局结束信息（覆盖写）
///   events.jsonl       事件流（追加，缎带/炮弹/成就等）
/// 与 tempArenaInfo.json 同款"只读文件"模式，不向游戏写入任何内容。
/// </summary>
public sealed class ModDataMonitor
{
    private readonly string _dataDir;
    private readonly object _lock = new();

    // 缓存：最近读取到的数据（供 GetLiveSummary 重复调用，避免重复 IO）
    private JsonObject? _battle;
    private JsonObject? _state;
    private JsonObject? _battleEnd;
    private bool _modReady;
    private bool _connecting;
    private DateTime _modReadyWriteTime = DateTime.MinValue;
    private List<JsonObject> _recentEvents = new();
    private DateTime _battleWriteTime = DateTime.MinValue;
    private DateTime _stateWriteTime = DateTime.MinValue;
    private DateTime _battleEndWriteTime = DateTime.MinValue;
    private DateTime _eventsWriteTime = DateTime.MinValue;
    private long _eventsFileSize = -1;
    private DateTime _statsParsedWriteTime = DateTime.MinValue;
    private JsonObject? _statsParsed;

    public ModDataMonitor(string dataDir)
    {
        _dataDir = dataDir;
    }

    /// <summary>数据目录是否存在（模组安装并已写入过数据）。</summary>
    public bool IsAvailable
    {
        get
        {
            try { return Directory.Exists(_dataDir); }
            catch { return false; }
        }
    }

    /// <summary>是否有正在进行的对局（mod 端 active=true 且无 battle_end）。</summary>
    public bool InBattle
    {
        get { lock (_lock) return _state is JsonObject st && st["active"]?.GetValue<bool>() == true; }
    }

    /// <summary>当前对局开始时间戳（battle.json 的 ts，仅 event=battle_start；无数据返回 null）。
    /// 用于检测是否进入新对局：值变化 = 新对局，应断开上一局的 AI 会话上下文。</summary>
    public string? CurrentBattleStartTs
    {
        get
        {
            lock (_lock)
            {
                if (_battle == null) return null;
                try
                {
                    return _battle["event"]?.GetValue<string>() == "battle_start"
                        ? _battle["ts"]?.GetValue<string>() : null;
                }
                catch { return null; }
            }
        }
    }

    /// <summary>mod 是否已加载并写过 ready 标记（mod_ready.json 存在）。</summary>
    public bool ModReady
    {
        get { lock (_lock) return _modReady; }
    }

    /// <summary>
    /// 轮询检查数据更新（应随主轮询定时器调用，如 500ms 一次）。
    /// 只读文件，全部容错，任何异常不影响主流程。
    /// </summary>
    public void CheckNow()
    {
        try
        {
            if (!IsAvailable) return;

            lock (_lock)
            {
                var readyFile = Path.Combine(_dataDir, "mod_ready.json");
                var readyFi = new FileInfo(readyFile);
                if (readyFi.Exists)
                {
                    // 更新时重读状态：status=connected 且 subscribed>0 才算真正连接；
                    // status=connecting 表示 mod 已加载、正在等待事件系统就绪（会自动重试）。
                    if (TryReadNewer(readyFile, ref _modReadyWriteTime, out var ready) && ready != null)
                    {
                        var status = ready["status"]?.GetValue<string>() ?? "connected"; // 旧格式无 status=已连接
                        int subscribed = 0;
                        try { subscribed = ready["subscribed"]?.GetValue<int>() ?? 0; } catch { }
                        _modReady = status == "connected" && subscribed > 0;
                        _connecting = status == "connecting";
                    }
                    // 心跳新鲜度：mod 每 30 秒刷新 ready；超过 90 秒未更新视为断开
                    //（游戏已退出、mod 未加载或已失效）。
                    if (DateTime.UtcNow - readyFi.LastWriteTimeUtc > TimeSpan.FromSeconds(90))
                    {
                        _modReady = false;
                        _connecting = false;
                    }
                }
                else
                {
                    _modReady = false;
                    _connecting = false;
                }

                var battleFile = Path.Combine(_dataDir, "battle.json");
                if (TryReadNewer(battleFile, ref _battleWriteTime, out var battle))
                    _battle = battle;

                var stateFile = Path.Combine(_dataDir, "state.json");
                if (TryReadNewer(stateFile, ref _stateWriteTime, out var state))
                    _state = state;

                var endFile = Path.Combine(_dataDir, "battle_end.json");
                if (TryReadNewer(endFile, ref _battleEndWriteTime, out var end))
                    _battleEnd = end;

                var statsParsedFile = Path.Combine(_dataDir, "stats_parsed.json");
                if (TryReadNewer(statsParsedFile, ref _statsParsedWriteTime, out var sp))
                    _statsParsed = sp;

                var eventsFile = Path.Combine(_dataDir, "events.jsonl");
                var fi = new FileInfo(eventsFile);
                if (fi.Exists && (fi.LastWriteTimeUtc != _eventsWriteTime || fi.Length != _eventsFileSize))
                {
                    _eventsWriteTime = fi.LastWriteTimeUtc;
                    _eventsFileSize = fi.Length;
                    _recentEvents = ReadRecentEvents(eventsFile, 20);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"模组数据读取异常（忽略）: {ex.Message}");
        }
    }

    /// <summary>实时战况浮窗需要的结构化状态（连接状态 + 对局状态 + 关键指标 + 最近事件）。</summary>
    public ModOverlayState GetOverlayState()
    {
        lock (_lock)
        {
            var state = _state;
            var battle = _battle;
            var battleEnd = _battleEnd;

            var inBattle = state != null && state["active"]?.GetValue<bool>() == true;
            var modReady = _modReady;
            var connecting = _connecting;

            var player = battle?["player"]?.GetValue<string>() ?? "";
            var battleType = battle?["battleType"]?.GetValue<string>()
                             ?? battle?["matchGroup"]?.GetValue<string>() ?? "";

            double? GetNum(JsonObject? o, string key)
            {
                if (o?[key] == null) return null;
                try { return o![key]!.GetValue<double>(); } catch { return null; }
            }

            var events = _recentEvents
                .Where(e => e["type"]?.GetValue<string>() is "ribbon" or "achievement" or "shell")
                .TakeLast(6)
                .Select(e =>
                {
                    var type = e["type"]?.GetValue<string>() ?? "";
                    return $"{type}: {DescribeEvent(type, e["data"] as JsonObject)}";
                })
                .ToList();

            return new ModOverlayState(
                ModReady: modReady,
                Connecting: connecting,
                InBattle: inBattle,
                BattleEnded: battleEnd != null,
                Player: player,
                BattleType: battleType,
                MyHealth: GetNum(state, "my_health"),
                MyMaxHealth: GetNum(state, "my_max_health"),
                DamageDealt: GetNum(state, "damageDealt"),
                DamageReceived: GetNum(state, "damageReceived"),
                Frags: GetNum(state, "frags"),
                Spotted: GetNum(state, "spotted"),
                Shots: GetNum(state, "shots"),
                Hits: GetNum(state, "hits"),
                RecentEventLines: events);
        }
    }

    /// <summary>构造注入 AI 的实时战况摘要文本（无数据时返回空串）。</summary>
    public string GetLiveSummary()
    {
        try
        {
            lock (_lock)
            {
                if (_state == null && _battle == null) return "";

                var sb = new System.Text.StringBuilder();

                var battleType = _battle?["event"]?.GetValue<string>() ?? "";
                var battleTs = _battle?["ts"]?.GetValue<string>() ?? "";
                if (battleType == "battle_start")
                {
                    sb.AppendLine($"- 对局开始时间：{battleTs}");
                    var mode = _battle?["battleType"]?.GetValue<string>() ?? _battle?["matchGroup"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(mode)) sb.AppendLine($"- 对局类型：{mode}");
                    var player = _battle?["player"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(player)) sb.AppendLine($"- 当前玩家：{player}");
                }

                if (_battleEnd != null)
                {
                    sb.AppendLine("- 对局已结束（模组检测到 battle_end）");
                    var result = _battleEnd?["battleResult"]?.GetValue<string>()
                                 ?? _battleEnd?["result"]?.GetValue<string>()
                                 ?? _battleEnd?["winnerTeam"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(result)) sb.AppendLine($"- 结束信息：{result}");
                }

                if (_state != null && _state["active"]?.GetValue<bool>() == true)
                {
                    sb.AppendLine("- 实时战斗统计（截至最近一次更新）：");
                    AppendNumber(sb, _state, "frags", "击杀");
                    AppendNumber(sb, _state, "damageDealt", "造成伤害");
                    AppendNumber(sb, _state, "damageReceived", "受到伤害");
                    AppendNumber(sb, _state, "shots", "开火");
                    AppendNumber(sb, _state, "hits", "命中");
                    AppendNumber(sb, _state, "spotted", "点亮");
                    AppendNumber(sb, _state, "my_health", "我的血量");
                    AppendNumber(sb, _state, "my_max_health", "我的最大血量");
                    AppendNumber(sb, _state, "teamHP", "我方总血量");
                    AppendNumber(sb, _state, "enemyHP", "敌方总血量");
                }

                // 战后完整统计（onBattleStatsReceived 解析，对局结束/复盘用）
                if (_statsParsed != null)
                {
                    sb.AppendLine("- 战后统计（本局结束）:");
                    AppendNumber(sb, _statsParsed, "damage", "总伤害");
                    AppendNumber(sb, _statsParsed, "ships_killed", "击毁");
                    AppendNumber(sb, _statsParsed, "pierced_hits_main", "主炮穿透");
                    AppendNumber(sb, _statsParsed, "hits_main", "主炮命中");
                    AppendNumber(sb, _statsParsed, "citadels", "核心区");
                    AppendNumber(sb, _statsParsed, "first_ships_spotted", "点亮");
                    AppendNumber(sb, _statsParsed, "received_damage_sum", "受击伤害");
                    AppendNumber(sb, _statsParsed, "battle_type", "模式");
                }

                // 最近事件（取最有信息量的）
                var meaningful = _recentEvents
                    .Where(e => e["type"]?.GetValue<string>() is "ribbon" or "achievement" or "shell")
                    .TakeLast(12).ToList();
                if (meaningful.Count > 0)
                {
                    sb.AppendLine("- 最近战况事件：");
                    foreach (var e in meaningful)
                    {
                        var type = e["type"]?.GetValue<string>() ?? "";
                        var data = e["data"] as JsonObject;
                        sb.AppendLine($"  · {type}: {DescribeEvent(type, data)}");
                    }
                }

                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"构造模组摘要失败（忽略）: {ex.Message}");
            return "";
        }
    }

    private static string DescribeEvent(string type, JsonObject? data)
    {
        if (data == null) return "(无详情)";
        return type switch
        {
            "ribbon" => DescribeRibbon(data),
            "achievement" => JoinFields(data, "achievement", "name"),
            "shell" => DescribeShell(data),
            _ => data.ToJsonString()
        };
    }

    /// <summary>缎带事件：主炮穿透 x1、跳弹 x1…（mod 侧已按 RibbonsType 映射成中文名）</summary>
    private static string DescribeRibbon(JsonObject data)
    {
        var name = data["name"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                if (data["count"]?.GetValue<long>() is long cnt && cnt > 1)
                    return $"{name} x{cnt}";
            }
            catch { }
            return name;
        }
        return JoinFields(data, "kind", "count");
    }

    /// <summary>炮弹事件：伤害 413（穿透）等（ModsAPI 官方文档 10 参数解析）</summary>
    private static string DescribeShell(JsonObject data)
    {
        var parts = new List<string>();
        try
        {
            if (data["damage"]?.GetValue<long>() is long dmg && dmg > 0)
                parts.Add($"伤害 {dmg}");
        }
        catch { }
        if (data["flags"] is JsonArray arr)
        {
            var flags = new List<string>();
            foreach (var n in arr)
            {
                try { var s = n!.GetValue<string>(); if (!string.IsNullOrEmpty(s)) flags.Add(s); } catch { }
            }
            if (flags.Count > 0) parts.Add(string.Join("+", flags));
        }
        return parts.Count > 0 ? string.Join("，", parts) : JoinFields(data, "victimId", "shooterId", "damage");
    }

    private static string JoinFields(JsonObject data, params string[] keys)
    {
        var parts = new List<string>();
        foreach (var k in keys)
        {
            if (data[k] != null)
            {
                var v = data[k]!.ToJsonString();
                if (v.Length > 40) v = v[..40];
                parts.Add($"{k}={v}");
            }
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "(无详情)";
    }

    private static void AppendNumber(StringBuilder sb, JsonObject obj, string key, string label)
    {
        if (obj[key] != null)
        {
            try
            {
                var v = obj[key]!.GetValue<object>();
                sb.AppendLine($"  · {label}：{Convert.ToString(v, CultureInfo.InvariantCulture)}");
            }
            catch { /* 非数值字段跳过 */ }
        }
    }

    /// <summary>读取最近 N 条事件（JSON Lines，倒序取尾部）。</summary>
    private static List<JsonObject> ReadRecentEvents(string path, int max)
    {
        var result = new List<JsonObject>();
        try
        {
            if (!File.Exists(path)) return result;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            foreach (var l in lines.TakeLast(max))
            {
                try
                {
                    if (JsonNode.Parse(l) is JsonObject o) result.Add(o);
                }
                catch { /* 单行损坏跳过 */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取事件流失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// 若文件写入时间比上次新则返回 true 并尝试解析（内容为空/无效时 obj=null，
    /// 调用方以新值为准——mod 在开局会清空 battle_end.json，残留缓存必须被清除，
    /// 否则助手会一直显示"对局结束"）。
    /// </summary>
    private static bool TryReadNewer(string path, ref DateTime lastWrite, out JsonObject? obj)
    {
        obj = null;
        try
        {
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            if (fi.LastWriteTimeUtc <= lastWrite) return false;
            lastWrite = fi.LastWriteTimeUtc;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return true; // 被清空：通知调用方清除缓存
            obj = JsonNode.Parse(text) as JsonObject;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>实时战况浮窗的一次完整状态快照（全部字段可空/可为空列表，浮窗端负责兜底显示）。</summary>
public sealed record ModOverlayState(
    bool ModReady,
    bool Connecting,
    bool InBattle,
    bool BattleEnded,
    string Player,
    string BattleType,
    double? MyHealth,
    double? MyMaxHealth,
    double? DamageDealt,
    double? DamageReceived,
    double? Frags,
    double? Spotted,
    double? Shots,
    double? Hits,
    IReadOnlyList<string> RecentEventLines);
