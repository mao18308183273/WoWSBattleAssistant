using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 监控《战舰世界》replay 目录下的 tempArenaInfo.json。
/// 游戏在对局加载时自动写入该文件，包含全部玩家名/shipId/阵营数据。
/// 支持两种格式：纯 JSON 和二进制包装（前4字节 0x12 0x32 0x34 0x11）。
/// 参考 ApeRadar 的同名实现（MIT 许可证）。
/// </summary>
public sealed class GameFileMonitor
{
    private DateTimeOffset _latestWriteTime = DateTimeOffset.MinValue;

    /// <summary>重置监控状态（清空阵容后调用），使同一场对局也能被重新检测。</summary>
    public void ResetWatch() => _latestWriteTime = DateTimeOffset.MinValue;

    /// <summary>查找最新的 tempArenaInfo.json 并判断是否有新对局。
    /// 传入游戏根目录路径，返回文件路径，若无新对局返回空字符串。</summary>
    public string GetLatestTempArenaInfoFile(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return "";

        var replayDir = Path.Combine(gamePath, "replays");
        if (!Directory.Exists(replayDir))
            return "";

        var files = Directory.GetFiles(replayDir, "tempArenaInfo.json", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            _latestWriteTime = DateTimeOffset.MinValue;
            return "";
        }

        DateTimeOffset newest = DateTimeOffset.MinValue;
        string newestFile = "";
        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(f);
                if (fi.LastWriteTime > newest)
                {
                    newest = fi.LastWriteTime;
                    newestFile = f;
                }
            }
            catch { /* 文件可能被占用跳过 */ }
        }

        if (newest <= _latestWriteTime)
            return ""; // 无新文件

        _latestWriteTime = newest;
        AppLog.Info($"检测到新 tempArenaInfo: {Path.GetFileName(newestFile)} ({newest:HH:mm:ss.fff})");
        return newestFile;
    }

    /// <summary>解析 tempArenaInfo.json，返回对局检测结果。</summary>
    public BattleDetectionResult ParseTempArenaInfo(string filePath)
    {
        var result = new BattleDetectionResult();
        try
        {
            var json = ReadTempArenaInfoFile(filePath);
            result.BattleType = json["matchGroup"]?.ToString() ?? "";
            result.BattleStartTime = json["dateTime"]?.ToString() ?? "";
            // 行动/剧情（PVE）才有 scenario；用它去查出生点知识库（Tools/spawn_db.json 的键）
            result.Scenario = json["scenario"]?.ToString() ?? "";
            result.MapName = json["mapDisplayName"]?.ToString() ?? "";
            result.PlayersPerTeam = json["playersPerTeam"]?.GetValue<int>() ?? 0;

            // 以前被忽略、但对 AI 判断很有价值的字段
            result.MapId = json["mapId"]?.GetValue<int>() ?? 0;
            result.Duration = json["duration"]?.GetValue<int>() ?? 0;
            result.GameMode = json["gameMode"]?.GetValue<int>() ?? 0;
            result.GameType = json["gameType"]?.ToString() ?? "";
            result.EventType = json["eventType"]?.ToString() ?? "";
            result.ClientVersion = json["clientVersionFromExe"]?.ToString() ?? "";
            result.Weather = FlattenWeather(json["weatherParams"]);

            var vehicles = json["vehicles"] as JsonArray;
            if (vehicles == null) return result;

            // 注意：此处不使用知识库（知识库尚未加载时也能返回阵容）
            // 只从 tempArenaInfo.json 自身的字段中读取可能存在的舰船相关信息。
            foreach (var v in vehicles)
            {
                if (v is not JsonObject vo) continue;

                var name = vo["name"]?.ToString() ?? "";
                var relation = vo["relation"]?.GetValue<int>() ?? 0;
                var shipId = vo["shipId"]?.GetValue<long>() ?? 0;
                var playerId = vo["id"]?.GetValue<int>() ?? 0;

                // 尝试读取可能存在的舰船字段：不同版本/模式下可能有 ship_name /
                // vehicleName / params 等；如果没有则保持 null，后续 ApplyLineupDetection
                // 在知识库未命中时用 "舰船(shipId)" 作为降级显示。
                var shipRawName =
                    vo["ship_name"]?.ToString() ??
                    vo["shipName"]?.ToString() ??
                    vo["vehicle_name"]?.ToString() ??
                    vo["title"]?.ToString() ??
                    vo["type"]?.ToString();

                var shipParams = vo["ship_params"]?.ToJsonString();

                // bot（playerId <= 30，出现在剧情/护航/行动等 PVE 模式中）不丢弃，
                // 而是标记 IsBot=true 保留：这样行动模式的敌方 AI 阵容、舰船参数都能进入知识库，
                // 且威胁清单会明确标注 [AI] 而不是让敌我信息缺失。
                var isBot = playerId <= 30;

                result.Players.Add(new DetectedPlayer
                {
                    PlayerName = name,
                    ShipId = shipId,
                    ShipRawName = shipRawName,
                    ShipParams = shipParams,
                    Relation = relation,
                    IsBot = isBot,
                });
            }

            result.Success = result.Players.Count > 0;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        return result;
    }

    /// <summary>
    /// 把 weatherParams（形如 {"0":["PCOW003_Cloudy"],"1":["PCOW005_Evening"]}）
    /// 压成一段人/AI 都能读的中文。天气直接影响视野与隐蔽，是判断"会不会被点亮"的关键。
    /// 未知代号保留原名，避免误译。
    /// </summary>
    private static string FlattenWeather(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node == null) return "";
        try
        {
            var parts = new System.Collections.Generic.List<string>();
            System.Action<System.Text.Json.Nodes.JsonNode?> take = null!;
            take = n =>
            {
                if (n == null) return;
                if (n is System.Text.Json.Nodes.JsonArray arr)
                {
                    foreach (var e in arr) take(e);
                    return;
                }
                if (n is System.Text.Json.Nodes.JsonObject obj)
                {
                    foreach (var kv in obj) take(kv.Value);
                    return;
                }
                var s2 = n.ToString();
                if (!string.IsNullOrWhiteSpace(s2)) parts.Add(s2);
            };
            take(node);

            var zhs = new System.Collections.Generic.List<string>();
            foreach (var s in parts)
            {
                // 形如 PCOW003_Cloudy —— 取下划线后的语义部分
                var key = s.Contains('_') ? s.Substring(s.LastIndexOf('_') + 1) : s;
                var zh = key switch
                {
                    "Clear" => "晴朗",
                    "Cloudy" => "多云",
                    "Overcast" => "阴天",
                    "Rain" => "雨",
                    "Storm" => "暴风雨",
                    "Fog" => "雾",
                    "Snow" => "雪",
                    "Dawn" => "黎明",
                    "Day" => "白天",
                    "Evening" => "黄昏",
                    "Night" => "夜晚",
                    _ => key,
                };
                if (!zhs.Contains(zh)) zhs.Add(zh);
            }
            return zhs.Count > 0 ? string.Join("、", zhs) : "";
        }
        catch
        {
            return node.ToJsonString();
        }
    }

    /// <summary>读取 tempArenaInfo.json，自动处理纯 JSON 和二进制包装两种格式。</summary>
    private static JsonNode ReadTempArenaInfoFile(string filePath)
    {
        string jsonText;
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var buffer = new byte[4];

        int firstByte = sr.Peek();

        if (firstByte == 0x7B) // '{' — 纯 JSON
        {
            jsonText = sr.ReadToEnd();
        }
        else if (firstByte == 0x12) // 二进制包装
        {
            fs.Seek(0, SeekOrigin.Begin);
            fs.ReadExactly(buffer, 0, 4);
            if (!buffer.SequenceEqual(new byte[] { 0x12, 0x32, 0x34, 0x11 }))
                throw new InvalidDataException("tempArenaInfo 文件格式不识别。");

            fs.Seek(8, SeekOrigin.Begin);
            fs.ReadExactly(buffer, 0, 4);
            sr.DiscardBufferedData();
            int dataLen = BitConverter.ToInt32(buffer, 0);
            long remaining = fs.Length - fs.Position;

            // 防御性解析：偏移 8 处按"长度字段"读取。若读到的值明显不合理
            // （≤0 或超过文件剩余+头部），说明该文件实际布局是
            // "魔数(4) + 长度(4) + 数据"（长度在偏移 4），此时回退为读取全部剩余文本，
            // 避免把数据前 4 字节误当长度、导致 JSON 解析失败。
            if (dataLen <= 0 || dataLen > remaining + 8)
            {
                jsonText = sr.ReadToEnd();
            }
            else
            {
                jsonText = sr.ReadToEnd()[..Math.Min(dataLen, (int)remaining)];
            }
        }
        else
        {
            throw new InvalidDataException("tempArenaInfo 文件格式不识别。");
        }

        var node = JsonNode.Parse(jsonText)
            ?? throw new InvalidDataException("tempArenaInfo JSON 解析失败。");
        return node;
    }

    /// <summary>从 clientrunner.log 自动检测所在服务器。</summary>
    public static string AutoDetectServer(string gamePath)
    {
        try
        {
            var logPath = Path.Combine(gamePath, "profile", "clientrunner.log");
            if (!File.Exists(logPath)) return "";

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            int idx = text.LastIndexOf("Selected realm: ");
            if (idx < 0) return "";
            idx += 16;
            int end = text.IndexOf('\n', idx);
            if (end < 0) end = text.Length;
            var realm = text[idx..end].Trim().ToLowerInvariant();
            return realm switch
            {
                "ru" => "ru",
                "eu" => "eu",
                "na" => "na",
                "asia" => "asia",
                "cn" => "cn",
                _ => ""
            };
        }
        catch { return ""; }
    }
}

/// <summary>对局检测结果，包含全部玩家信息和基本元数据。</summary>
public sealed class BattleDetectionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string BattleType { get; set; } = "";
    public string BattleStartTime { get; set; } = "";

    /// <summary>剧本代号（行动/剧情模式才有，如 PCVO004_OP_01_04_s02_Naval_Defense_HIGH_LVL）。随机战为空。</summary>
    public string Scenario { get; set; } = "";

    /// <summary>地图显示名（如 s02_Naval_Defense）。</summary>
    public string MapName { get; set; } = "";

    /// <summary>每队人数（行动模式常为 7）。</summary>
    public int PlayersPerTeam { get; set; }

    // ------------------------------------------------------------
    // 以下字段 tempArenaInfo.json 里一直都有，但以前从没读进来。
    // 它们都是给 AI 的高质量上下文：以前 AI 只能靠小地图截图去"猜"地图和天气。
    // ------------------------------------------------------------

    /// <summary>地图内部 id（如 10）。</summary>
    public int MapId { get; set; }

    /// <summary>对局总时长（秒，常见 1200）。有了它 AI 才知道"还剩多久"。</summary>
    public int Duration { get; set; }

    /// <summary>游戏模式号（如 7）。</summary>
    public int GameMode { get; set; }

    /// <summary>游戏类型字符串（如 EventBattle）。</summary>
    public string GameType { get; set; } = "";

    /// <summary>事件代号（如 PCVE027），某些运营活动的标识。</summary>
    public string EventType { get; set; } = "";

    /// <summary>
    /// 天气参数（weatherParams，形如 {"0":["PCOW003_Cloudy"],...}）。
    /// 天气直接影响视野与隐蔽 —— 对 AI 判断"能不能被点亮"很关键。
    /// </summary>
    public string Weather { get; set; } = "";

    /// <summary>
    /// 客户端版本（如 "15,8,1,13243917"）。可用来校验 mod 与当前客户端是否匹配，
    /// 也能在 mod 失效时给出明确提示，而不是默默没数据。
    /// </summary>
    public string ClientVersion { get; set; } = "";

    public List<DetectedPlayer> Players { get; set; } = new();

    /// <summary>把上面这些元数据整理成一段可以直接喂给 AI 的文本。</summary>
    public string BuildContextText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【对局基本信息（tempArenaInfo.json，权威值）】");
        sb.AppendLine($"模式：{BattleType}{(string.IsNullOrEmpty(GameType) ? "" : " / " + GameType)}" +
                      $"{(GameMode > 0 ? "（gameMode=" + GameMode + "）" : "")}");
        if (!string.IsNullOrEmpty(MapName))
            sb.AppendLine($"地图：{MapName}{(MapId > 0 ? "（mapId=" + MapId + "）" : "")}");
        if (!string.IsNullOrEmpty(Weather))
            sb.AppendLine($"天气：{Weather}");
        if (Duration > 0)
            sb.AppendLine($"本局时长上限：{Duration} 秒（{Duration / 60} 分 {Duration % 60} 秒）");
        if (PlayersPerTeam > 0)
            sb.AppendLine($"每队人数：{PlayersPerTeam}");
        if (!string.IsNullOrEmpty(EventType))
            sb.AppendLine($"事件代号：{EventType}");
        if (!string.IsNullOrEmpty(ClientVersion))
            sb.AppendLine($"客户端版本：{ClientVersion}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>tempArenaInfo.json 中解析出的单个玩家。</summary>
public sealed class DetectedPlayer
{
    /// <summary>玩家名（含 [军团] 标签）</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>游戏内 shipId（数字）</summary>
    public long ShipId { get; set; }

    /// <summary>舰船原名（如果 tempArenaInfo.json 中提供了该字段）</summary>
    public string? ShipRawName { get; set; }

    /// <summary>舰船等级/类型/参数（如果 tempArenaInfo.json 中提供了 ship_params）</summary>
    public string? ShipParams { get; set; }

    /// <summary>阵营: 0=自己, 1=队友, 2=敌方</summary>
    public int Relation { get; set; }

    /// <summary>是否为 bot/AI（行动/剧情等 PVE 模式的电脑玩家；不查战绩，威胁清单标注 [AI]）</summary>
    public bool IsBot { get; set; }
}
