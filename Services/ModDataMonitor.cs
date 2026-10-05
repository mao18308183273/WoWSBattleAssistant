using System.Globalization;
using System.IO;
using System.Linq;
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

    /// <param name="dataDir">mod 数据目录</param>
    /// <param name="shipNameResolver">
    /// 可选：全局船 id -> 中文舰名。传入后花名册会显示中文舰名而不是内部名。
    /// 调用方一般传 <c>id =&gt; _database.GetShipDisplayName(id)</c>。
    /// </param>
    public ModDataMonitor(string dataDir, Func<long, string>? shipNameResolver = null)
    {
        _dataDir = dataDir;
        _shipNameResolver = shipNameResolver;
    }

    /// <summary>全局船 id -> 中文舰名（可选，未设置时退回内部名）。</summary>
    private readonly Func<long, string>? _shipNameResolver;

    private DateTime _playersWriteTime = DateTime.MinValue;
    private List<ModPlayer> _players = new();
    private DateTime _statsFullWriteTime = DateTime.MinValue;
    private JsonObject? _statsFull;

    // —— 逐舰伤害归属 ——
    // 实测结论：mod 的 shell 事件里 shooterId / victimId 与 players.json 的 arenaShipId
    // 完全同域（同一局内 123/123 条事件全部可映射到具体舰船），因此"我打谁多少、
    // 谁打我多少"是可以直接算出来的，不需要任何坐标数据。
    private long _myArenaShipId;
    private long _eventsReadOffset;
    private readonly Dictionary<long, long> _dmgDealtByTarget = new();
    private readonly Dictionary<long, long> _dmgTakenFromSource = new();
    private long _dmgDealtTotal;
    private long _dmgTakenTotal;
    private readonly Dictionary<string, long> _ribbonTotals = new();
    private string? _aggBattleTs;

    /// <summary>
    /// 当前对局的剧本代号（行动/剧情模式的 scenario，如
    /// PCVO004_OP_01_04_s02_Naval_Defense_HIGH_LVL）。由外部从 tempArenaInfo.json 填入——
    /// mod 侧拿不到剧本号，但出生点知识库以它为键。
    /// </summary>
    public string? CurrentScenario { get; set; }

    /// <summary>当前地图显示名（如 s02_Naval_Defense），知识库匹配兜底用。</summary>
    public string? CurrentMapName { get; set; }

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

                // 实时花名册（mod v2：每 2 秒落盘，权威 teamId / isBot / isAlive + 全局船 id）
                var playersFile = Path.Combine(_dataDir, "players.json");
                if (TryReadNewer(playersFile, ref _playersWriteTime, out var pl))
                    _players = ParsePlayers(pl);

                // 战后逐敌交互明细（mod v2）
                var statsFullFile = Path.Combine(_dataDir, "stats_full.json");
                if (TryReadNewer(statsFullFile, ref _statsFullWriteTime, out var sf))
                    _statsFull = sf;

                var eventsFile = Path.Combine(_dataDir, "events.jsonl");
                var fi = new FileInfo(eventsFile);

                // 新对局：events.jsonl 被 mod 清空重写 → 累计量全部归零
                var battleTs = CurrentBattleStartTs;
                if (battleTs != null && battleTs != _aggBattleTs)
                {
                    _aggBattleTs = battleTs;
                    _dmgDealtByTarget.Clear();
                    _dmgTakenFromSource.Clear();
                    _ribbonTotals.Clear();
                    _dmgDealtTotal = 0;
                    _dmgTakenTotal = 0;
                    _eventsReadOffset = 0;
                    _myArenaShipId = 0;
                }

                if (fi.Exists && (fi.LastWriteTimeUtc != _eventsWriteTime || fi.Length != _eventsFileSize))
                {
                    _eventsWriteTime = fi.LastWriteTimeUtc;
                    _eventsFileSize = fi.Length;
                    _recentEvents = ReadRecentEvents(eventsFile, 20);
                    AggregateEvents(eventsFile);
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
                    return $"{type}: {DescribeEvent(type, e)}";   // 事件是扁平结构
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
                // 只有花名册也算有数据（mod v2 每 2 秒落盘 players.json）
                if (_state == null && _battle == null && !HasRoster && _statsFull == null)
                    return "";

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

                // 战后逐敌交互明细（mod v2：stats_full.json，interactions 按类别分组）
                if (_statsFull != null)
                {
                    var inter = _statsFull["interactions"] as JsonObject;
                    if (inter != null)
                    {
                        sb.AppendLine("- 战后逐敌交互明细（玩家 id 为游戏内 playerId）：");
                        foreach (var kv in inter)
                        {
                            var cat = kv.Key;
                            if (kv.Value is not JsonArray rows || rows.Count == 0) continue;
                            var parts = new List<string>();
                            foreach (var r in rows)
                            {
                                if (r is not JsonObject ro) continue;
                                var pid = ro["playerId"]?.ToString() ?? "?";
                                var dmgAll = ro["damage_all"]?.GetValue<long>() ?? 0;
                                var dmgMain = ro["damage_main"]?.GetValue<long>() ?? 0;
                                var hits = ro["hits_main"]?.GetValue<long>() ?? 0;
                                var cit = ro["citadels"]?.GetValue<long>() ?? 0;
                                var fires = ro["fires"]?.GetValue<long>() ?? 0;
                                var floods = ro["floods"]?.GetValue<long>() ?? 0;
                                var mc = ro["module_crits"]?.GetValue<long>() ?? 0;
                                var mb = ro["module_breaks"]?.GetValue<long>() ?? 0;
                                var seg = new List<string>();
                                if (dmgAll > 0) seg.Add($"总伤{dmgAll}");
                                if (dmgMain > 0) seg.Add($"炮伤{dmgMain}");
                                if (hits > 0) seg.Add($"命中{hits}");
                                if (cit > 0) seg.Add($"核心{cit}");
                                if (fires > 0) seg.Add($"起火{fires}");
                                if (floods > 0) seg.Add($"进水{floods}");
                                if (mc > 0) seg.Add($"部件损坏{mc}");
                                if (mb > 0) seg.Add($"部件击毁{mb}");
                                if (seg.Count > 0) parts.Add($"{pid}({string.Join("/", seg)})");
                            }
                            if (parts.Count > 0)
                                sb.AppendLine($"  {cat}：" + string.Join("、", parts));
                        }
                    }
                }

                // 实时花名册：让 AI 知道"图上的一个点具体是谁"
                // （队伍规模不固定，15v15 / 7v7 / PVE 不定人数都能覆盖）
                var roster = GetRosterText();
                if (!string.IsNullOrEmpty(roster)) sb.Append(roster);

                // 逐舰伤害归属（最硬的战术数据：完全来自游戏内炮弹事件，无任何推测）
                var attr = GetDamageAttributionText();
                if (!string.IsNullOrEmpty(attr)) sb.Append(attr);

                // 行动/剧情模式：AI 出生点预测（回放实测累计，玩得越多越准）
                var spawnBrief = GetSpawnBriefingText();
                if (!string.IsNullOrEmpty(spawnBrief)) sb.Append(spawnBrief);

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
                        // 注意：mod 写出的事件是扁平结构（字段就在本层），不是嵌套的 "data" 子对象。
                        // 旧代码取 e["data"] 恒为 null，导致所有事件都显示"(无详情)"。
                        sb.AppendLine($"  · {type}: {DescribeEvent(type, e)}");
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

    /// <summary>当前实时花名册（mod v2 的 players.json；无数据时空列表）。</summary>
    public IReadOnlyList<ModPlayer> Players
    {
        get { lock (_lock) return _players; }
    }

    /// <summary>是否有可用花名册数据。</summary>
    public bool HasRoster => Players.Count > 0;

    /// <summary>解析 players.json 的 players 数组。</summary>
    private static List<ModPlayer> ParsePlayers(JsonObject? root)
    {
        var list = new List<ModPlayer>();
        if (root == null) return list;
        var arr = root["players"] as JsonArray;
        if (arr == null) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            try
            {
                list.Add(new ModPlayer
                {
                    EntityId = o["entityId"]?.GetValue<long>() ?? 0,
                    Name = o["name"]?.ToString() ?? "",
                    TeamId = o["teamId"]?.GetValue<int>() ?? -1,
                    IsBot = o["isBot"]?.GetValue<bool>() ?? false,
                    IsOwn = o["isOwn"]?.GetValue<bool>() ?? false,
                    Alive = o["alive"]?.GetValue<bool>() ?? false,
                    MaxHealth = o["maxHealth"]?.GetValue<long>() ?? 0,
                    ShipGlobalId = o["shipGlobalId"]?.GetValue<long>() ?? 0,
                    ShipInternal = o["shipInternal"]?.ToString() ?? "",
                    Tier = o["tier"]?.GetValue<int>() ?? 0,
                    ArenaShipId = o["arenaShipId"]?.GetValue<long>() ?? 0,
                });
            }
            catch { /* 单行损坏跳过 */ }
        }
        return list;
    }

    private string ShipLabel(ModPlayer p)
    {
        var nm = "";
        if (_shipNameResolver != null && p.ShipGlobalId > 0)
        {
            try { nm = _shipNameResolver(p.ShipGlobalId) ?? ""; } catch { }
        }
        // 知识库未命中会返回 "未知舰船(123)"，这种情况退回内部名
        //（行动模式的剧本舰如 PASX005_St_Clair 不在公开舰船库里，必然走到这里）
        if (string.IsNullOrEmpty(nm) || nm.StartsWith("未知舰船", StringComparison.Ordinal))
            nm = PrettifyInternal(p.ShipInternal);
        if (string.IsNullOrEmpty(nm)) nm = "未知";
        return p.Tier > 0 ? $"T{p.Tier} {nm}" : nm;
    }

    /// <summary>
    /// 把游戏内部船名清洗成人能读的样子：PASX005_St_Clair → St Clair，
    /// PVSB010-Libertad → Libertad。内部名总比一串前缀+下划线好认。
    /// </summary>
    private static string PrettifyInternal(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw!;
        var i = s.IndexOf('_');
        if (i < 0) i = s.IndexOf('-');
        if (i >= 0 && i + 1 < s.Length) s = s[(i + 1)..];
        return s.Replace('_', ' ').Trim();
    }

    /// <summary>
    /// 行动/剧情模式下的「AI 出生点」简报。数据来自 Tools/spawn_db.json（回放实测累计）。
    /// 非 PVE、或知识库里没有这个剧本时返回空串（不污染随机战的输出）。
    /// </summary>
    public string GetSpawnBriefingText()
    {
        if (string.IsNullOrWhiteSpace(CurrentScenario) && string.IsNullOrWhiteSpace(CurrentMapName))
            return "";

        double elapsed = 0;
        var ts = CurrentBattleStartTs;
        if (!string.IsNullOrEmpty(ts) &&
            DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start))
        {
            elapsed = Math.Max(0, (DateTime.Now - start).TotalSeconds);
            // 兜底：开局时间戳是上一局残留（或系统时钟异常）时，别把 t 算成几十万秒，
            // 否则波次预测会直接跳到"全部已出现"。40 分钟足够覆盖任何一局。
            if (elapsed > 2400) { elapsed = 2400; AppLog.Warn($"出生点简报：开局时间戳异常（{ts}），t 已钳到 2400s"); }
        }

        var roles = Players.Where(p => p.IsScenarioBot)
                           .Select(p => p.Name)
                           .Where(n => !string.IsNullOrEmpty(n))!;

        return ScenarioSpawnKnowledge.BuildBriefing(CurrentScenario, CurrentMapName, elapsed, roles);
    }

    /// <summary>
    /// 构造注入 AI 的花名册文本（引擎/舰种/存活/人机全为游戏内权威值）。
    /// 队伍规模不固定（15v15 / 7v7 / PVE 不定人数），这里给多少写多少。
    /// </summary>
    public string GetRosterText()
    {
        var players = Players;
        if (players.Count == 0) return "";
        var sb = new StringBuilder();
        var ally = players.Where(p => p.TeamId == 0).ToList();
        var foe = players.Where(p => p.TeamId == 1).ToList();
        if (ally.Count == 0 && foe.Count == 0)
            foe = players.Where(p => p.TeamId != 0).ToList();

        // 行动/剧情（PVE 剧本）模式识别：剧本机器人名形如 IDS_OP_01_04_HELPME
        var isScenario = players.Any(p => p.IsScenarioBot);
        var protectTargets = players.Where(p =>
            p.ScenarioRole is "护送目标" or "重点保护目标" or "维修舰(保护目标)"
                             or "运输舰(保护目标)").ToList();

        sb.AppendLine("- 本局舰船清单（游戏内实时权威数据，[AI]=电脑玩家，[沉]=已阵亡"
                      + (isScenario ? "，[]内为剧本角色" : "") + "）：");
        if (isScenario)
            sb.AppendLine("  模式：行动/剧情（PVE 剧本）。敌方全部为 AI，无需查玩家战绩；"
                          + "剧本机器人随波次刷新，清单会变长。");
        if (ally.Count > 0)
        {
            sb.Append("  我方 " + ally.Count + "：");
            sb.AppendLine(string.Join("、", ally.Select(p =>
                ShipLabel(p) + (p.IsOwn ? "（我）" : "") + (p.IsBot ? "[AI]" : "") +
                RoleTag(p) + (p.Alive ? "" : "[沉]"))));
        }
        if (foe.Count > 0)
        {
            sb.Append("  敌方 " + foe.Count + "：");
            sb.AppendLine(string.Join("、", foe.Select(p =>
                ShipLabel(p) + (p.IsBot ? "[AI]" : "") +
                RoleTag(p) + (p.Alive ? "" : "[沉]"))));
        }
        var me = players.FirstOrDefault(p => p.IsOwn);
        if (me != null) sb.AppendLine($"  我的船：{ShipLabel(me)}（血量上限 {me.MaxHealth}）");
        sb.AppendLine($"  存活：我方 {ally.Count(p => p.Alive)}/{ally.Count}，"
                      + $"敌方 {foe.Count(p => p.Alive)}/{foe.Count}");
        if (protectTargets.Count > 0)
            sb.AppendLine($"  任务目标舰：{protectTargets.Count(p => p.Alive)}/{protectTargets.Count} 存活"
                          + "（" + string.Join("、", protectTargets.Select(p =>
                              ShipLabel(p) + (p.Alive ? "" : "[已损失]"))) + "）"
                          + " → 目标舰损失通常直接导致行动失败，优先拦截逼近它们的敌舰。");
        return sb.ToString();
    }

    private static string RoleTag(ModPlayer p)
    {
        var r = p.ScenarioRole;
        return string.IsNullOrEmpty(r) ? "" : "[" + r + "]";
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

    /// <summary>
    /// 增量消费 events.jsonl：按字节偏移只读新增部分，累计"我打谁 / 谁打我"的逐舰伤害。
    /// 只读、无锁重入（调用方已持有 _lock），任何异常都被吞掉。
    /// </summary>
    private void AggregateEvents(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _eventsReadOffset) _eventsReadOffset = 0;   // 文件被截断重写
            if (fs.Length == _eventsReadOffset) return;
            fs.Seek(_eventsReadOffset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject o) continue;
                    var type = o["type"]?.GetValue<string>() ?? "";
                    if (type == "shell")
                    {
                        var s = o["shooterId"]?.GetValue<long>() ?? 0;
                        var v = o["victimId"]?.GetValue<long>() ?? 0;
                        var d = o["damage"]?.GetValue<long>() ?? 0;
                        if (_myArenaShipId == 0)
                        {
                            // 自己的 arenaShipId 只能从花名册拿（events 里没有"我是谁"的标记）
                            var own = _players.FirstOrDefault(p => p.IsOwn);
                            if (own != null) _myArenaShipId = own.ArenaShipId;
                        }
                        if (_myArenaShipId == 0 || d <= 0) continue;
                        if (s == _myArenaShipId && v != _myArenaShipId)
                        {
                            _dmgDealtByTarget.TryGetValue(v, out var prev);
                            _dmgDealtByTarget[v] = prev + d;
                            _dmgDealtTotal += d;
                        }
                        else if (v == _myArenaShipId && s != _myArenaShipId)
                        {
                            _dmgTakenFromSource.TryGetValue(s, out var prev);
                            _dmgTakenFromSource[s] = prev + d;
                            _dmgTakenTotal += d;
                        }
                    }
                    else if (type == "ribbon")
                    {
                        var nm = o["name"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(nm))
                        {
                            long cnt = 1;
                            try { cnt = o["count"]?.GetValue<long>() ?? 1; } catch { }
                            if (cnt < 1) cnt = 1;
                            _ribbonTotals.TryGetValue(nm!, out var prev);
                            _ribbonTotals[nm!] = prev + cnt;
                        }
                    }
                }
                catch { /* 单条坏行跳过 */ }
            }
            _eventsReadOffset = fs.Position;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"伤害归属累计失败（忽略）: {ex.Message}");
        }
    }

    /// <summary>把 arenaShipId 解析成可读舰名（优先中文舰名，其次角色标签，最后内部名）。</summary>
    private string LabelByArena(long arenaId)
    {
        var p = _players.FirstOrDefault(x => x.ArenaShipId == arenaId);
        if (p == null) return $"#{arenaId}";
        var role = p.ScenarioRole;
        return string.IsNullOrEmpty(role) ? ShipLabel(p) : $"{ShipLabel(p)}[{role}]";
    }

    /// <summary>
    /// 逐舰伤害归属文本（本局累计）。这是目前能给 AI 的"最硬"的战术数据：
    /// 完全是游戏内权威值，不依赖坐标、不依赖截图、不依赖推测。
    /// </summary>
    public string GetDamageAttributionText()
    {
        lock (_lock)
        {
            if (_dmgDealtTotal <= 0 && _dmgTakenTotal <= 0 && _ribbonTotals.Count == 0)
                return "";
            var sb = new StringBuilder();
            sb.AppendLine("- 逐舰伤害归属（本局实时累计，来自游戏内炮弹事件，权威值）：");
            sb.AppendLine($"  我打出总伤害 {_dmgDealtTotal}，承受总伤害 {_dmgTakenTotal}");
            if (_dmgDealtByTarget.Count > 0)
            {
                var top = _dmgDealtByTarget.OrderByDescending(kv => kv.Value).Take(10);
                sb.AppendLine("  我对各目标的伤害：" + string.Join("、",
                    top.Select(kv => $"{LabelByArena(kv.Key)} {kv.Value}")));
            }
            if (_dmgTakenFromSource.Count > 0)
            {
                var top = _dmgTakenFromSource.OrderByDescending(kv => kv.Value).Take(10);
                sb.AppendLine("  各来源对我的伤害：" + string.Join("、",
                    top.Select(kv => $"{LabelByArena(kv.Key)} {kv.Value}")));
            }
            if (_ribbonTotals.Count > 0)
            {
                var top = _ribbonTotals.OrderByDescending(kv => kv.Value).Take(12);
                sb.AppendLine("  缎带累计：" + string.Join("、",
                    top.Select(kv => $"{kv.Key} x{kv.Value}")));
            }
            return sb.ToString();
        }
    }
}

/// <summary>实时花名册里的一名玩家（来自 mod v2 的 players.json，全部为游戏内权威值）。</summary>
public sealed record ModPlayer
{
    /// <summary>实体 id（与炮弹事件的 victimId / shooterId 同域，可打通"谁打谁"）</summary>
    public long EntityId { get; set; }

    /// <summary>玩家名</summary>
    public string Name { get; set; } = "";

    /// <summary>阵营：0=我方，1=敌方（游戏内 teamId，权威）</summary>
    public int TeamId { get; set; } = -1;

    /// <summary>是否电脑玩家（游戏内 isBot，权威；取代 playerId&lt;=30 的启发式）</summary>
    public bool IsBot { get; set; }

    /// <summary>是否自己</summary>
    public bool IsOwn { get; set; }

    /// <summary>是否存活（实时）</summary>
    public bool Alive { get; set; }

    /// <summary>血量上限</summary>
    public long MaxHealth { get; set; }

    /// <summary>全局船 id —— 与知识库 ship_id 同域，可用于精确查舰船参数</summary>
    public long ShipGlobalId { get; set; }

    /// <summary>游戏内部船名（如 PZSC109_Sejong），知识库未命中时的降级显示</summary>
    public string ShipInternal { get; set; } = "";

    /// <summary>等级</summary>
    public int Tier { get; set; }

    /// <summary>
    /// 本局临时舰船 id（arenaShipId）—— 与炮弹事件的 shooterId / victimId 完全同域，
    /// 是"谁打了谁、打了多少"能精确归到具体舰船的唯一钥匙（实测 123/123 命中）。
    /// 注意：它跟 EntityId 不是一回事，且每局重新分配，绝不能拿去查知识库。
    /// </summary>
    public long ArenaShipId { get; set; }

    /// <summary>
    /// 行动/剧情模式（PVE）下电脑舰的战术角色。由 mod 读到的 IDS_ 名字反推，
    /// 例如 IDS_OP_01_04_HELPME → 需保护目标。非剧本舰返回 null。
    /// </summary>
    public string? ScenarioRole => DescribeScenarioRole(Name);

    /// <summary>是否为行动/剧情模式的剧本舰（名字形如 IDS_OP_01_04_XXX）。</summary>
    public bool IsScenarioBot => !string.IsNullOrEmpty(Name) &&
                                 (Name.StartsWith("IDS_OP_", StringComparison.Ordinal) ||
                                  Name.StartsWith("IDS_ART_", StringComparison.Ordinal) ||
                                  Name.StartsWith("IDS_", StringComparison.Ordinal) &&
                                  Name.Contains("_BOT"));

    /// <summary>
    /// 把游戏内剧本机器人名（本地化 key）翻译成 AI 能直接用于决策的战术角色。
    /// 实测样本（行动"海军防御" PCVO004_OP_01_04）：
    ///   HELPME / PROTECTME / REPAIRSHIP → 我方需护送/保护的目标舰
    ///   ATAKER_C1..C5 / ATAKER_CR_13..16 / ATAKER_R9 → 进攻方敌舰（波次递增）
    ///   ART_DTBP_BOT_1..4 → 敌方小型快艇/鱼雷艇
    /// </summary>
    public static string? DescribeScenarioRole(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var n = name.ToUpperInvariant();
        if (n.Contains("HELPME")) return "护送目标";
        if (n.Contains("_ALLY") || n.Contains("ALLY_")) return "友军AI";
        if (n.Contains("PROTECTME")) return "重点保护目标";
        if (n.Contains("REPAIRSHIP")) return "维修舰(保护目标)";
        if (n.Contains("CONVOY") || n.Contains("TRANSPORT")) return "运输舰(保护目标)";
        if (n.Contains("ART_DTBP") || n.Contains("_BOT")) return "小型快艇";
        if (n.Contains("ATAKER") || n.Contains("ATTACKER"))
        {
            if (n.Contains("_CR_")) return "进攻方重巡";
            if (n.Contains("_BB_")) return "进攻方战列";
            if (n.Contains("_DD_")) return "进攻方驱逐";
            if (n.Contains("_CV_")) return "进攻方航母";
            if (n.Contains("_R")) return "进攻方远程火力";
            return "进攻方主力";
        }
        return null;
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
