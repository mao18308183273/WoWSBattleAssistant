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

                // 过滤 bot（id <= 30，出现在剧情/护航模式中）
                if (playerId <= 30) continue;

                result.Players.Add(new DetectedPlayer
                {
                    PlayerName = name,
                    ShipId = shipId,
                    ShipRawName = shipRawName,
                    ShipParams = shipParams,
                    Relation = relation,
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
            jsonText = sr.ReadToEnd()[..Math.Min(dataLen, (int)(fs.Length - fs.Position))];
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
    public List<DetectedPlayer> Players { get; set; } = new();
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
}
