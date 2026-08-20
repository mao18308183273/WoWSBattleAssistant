using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WoWSBattleAssistant.Models;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 从 AI 返回文本中解析阵容 JSON 的共享工具。
/// 供 DeepSeekVisionAnalyzer 和 OpenAICompatibleAnalyzer 共用。
/// </summary>
public static class LineupParser
{
    /// <summary>
    /// 从 AI 返回文本中提取配对列表（优先）或扁平舰船名列表（兼容），
    /// 结果写入 result 对象。
    /// </summary>
    public static void Parse(string content, ShipRecognitionResult result)
    {
        var text = content.Trim();
        var fenceMatch = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success) text = fenceMatch.Groups[1].Value.Trim();

        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first >= 0 && last > first)
            text = text.Substring(first, last - first + 1);

        try
        {
            var node = JsonNode.Parse(text);

            // 优先解析配对结构
            if (node?["pairs"] is JsonArray pairs)
            {
                foreach (var p in pairs)
                {
                    if (p is not JsonObject po) continue;
                    var player = po["player"]?.ToString()?.Trim() ?? "";
                    var ship = po["ship"]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(ship)) continue;
                    result.PlayerShipPairs.Add(new PlayerShipPair { Player = player, Ship = ship });
                    result.Ships.Add(ship);
                }
            }

            // 兼容旧格式：ships 扁平列表
            if (result.Ships.Count == 0 && node?["ships"] is JsonArray arr)
            {
                result.Ships = arr.Select(x => x?.ToString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
            }

            result.Success = result.Ships.Count > 0;
            if (!result.Success)
                result.Error = "AI 未识别到任何舰船名，请重试或手动输入。";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = "解析 AI 返回 JSON 失败: " + ex.Message + " | 原文: " + Truncate(content, 300);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}