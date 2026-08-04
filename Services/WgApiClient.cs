using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WoWSBattleAssistant.Models;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// WG Public API 战绩查询客户端。
/// 作为 shinoaki API 的备选/增强方案。
/// 支持按玩家名搜索 accountId，拉取账号战绩与单船战绩。
/// application_id 使用公开 ID（同 ApeRadar）。
/// 注意：WG Public API 不支持 RU（俄服）和 CN（国服），这两个区服需使用 Vortex API 或 shinoaki。
/// </summary>
public static class WgApiClient
{
    private const string WgBaseUrl = "https://api.{0}/wows/";
    private const string YuyukoProxyUrl = "https://dev-proxy.wows.shinoaki.com:7700/dev";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>服务器名到 WG API 域名的映射</summary>
    private static readonly Dictionary<string, string> ServerDomains = new()
    {
        ["eu"] = "worldofwarships.eu",
        ["na"] = "worldofwarships.com",
        ["asia"] = "worldofwarships.asia",
        ["ru"] = "korabli.su",
        ["cn"] = "wowsgame.cn",
    };

    /// <summary>批量按玩家名搜索 accountId。返回字典：playerName → accountId（未命中不包含）。</summary>
    public static async Task<Dictionary<string, long>> SearchPlayersAsync(
        List<string> playerNames, string server, string appId, bool useYuyukoProxy, CancellationToken ct)
    {
        var result = new Dictionary<string, long>();

        // 过滤掉以冒号开头的名字（人机），它们不可能在 WG API 中搜到
        var validNames = playerNames.Where(n => !string.IsNullOrWhiteSpace(n) && n[0] != ':').Distinct().ToList();
        if (validNames.Count == 0) return result;

        var nameList = string.Join("%2C", validNames.Select(Uri.EscapeDataString));
        var domain = ServerDomains.GetValueOrDefault(server, "worldofwarships.asia");
        var url = useYuyukoProxy
            ? $"{YuyukoProxyUrl}/wows/search/{server}/?type=exact&search={nameList}"
            : $"https://api.{domain}/wows/account/list/?application_id={appId}&type=exact&search={nameList}";

        try
        {
            var jsonText = await Http.GetStringAsync(url, ct);
            var node = JsonNode.Parse(jsonText);
            if (node?["status"]?.ToString() != "ok") return result;

            var data = node["data"] as JsonArray;
            if (data == null) return result;

            foreach (var item in data)
            {
                var name = item?["nickname"]?.ToString();
                var id = item?["account_id"]?.GetValue<long>();
                if (name != null && id.HasValue)
                    result[name] = id.Value;
            }
        }
        catch { /* 查询失败降级 */ }

        return result;
    }

    /// <summary>获取玩家战绩信息。返回 PlayerThreatInfo 或 null（未搜到/隐藏/异常）。</summary>
    public static async Task<PlayerThreatInfo?> GetPlayerInfoAsync(
        long accountId, string userName, string shipName, int relation,
        string server, string appId, bool useYuyukoProxy, CancellationToken ct)
    {
        var info = new PlayerThreatInfo
        {
            UserName = userName,
            ShipName = shipName,
            Relation = relation,
            SearchHit = true,
            HasColon = userName.Contains(':'),
            AccountId = accountId,
        };

        try
        {
            var domain = ServerDomains.GetValueOrDefault(server, "worldofwarships.asia");
            var url = useYuyukoProxy
                ? $"{YuyukoProxyUrl}/wows/account/info/{server}/?extra=statistics.pvp_solo%2Cstatistics.pvp_div2%2Cstatistics.pvp_div3&fields=hidden_profile%2Cstatistics.pvp.wins%2Cstatistics.pvp.battles%2Cstatistics.pvp_solo.wins%2Cstatistics.pvp_solo.battles%2Cstatistics.pvp_div2.wins%2Cstatistics.pvp_div2.battles%2Cstatistics.pvp_div3.wins%2Cstatistics.pvp_div3.battles&account_id={accountId}"
                : $"https://api.{domain}/wows/account/info/?application_id={appId}&extra=statistics.pvp_solo%2Cstatistics.pvp_div2%2Cstatistics.pvp_div3&fields=hidden_profile%2Cstatistics.pvp.wins%2Cstatistics.pvp.battles%2Cstatistics.pvp_solo.wins%2Cstatistics.pvp_solo.battles%2Cstatistics.pvp_div2.wins%2Cstatistics.pvp_div2.battles%2Cstatistics.pvp_div3.wins%2Cstatistics.pvp_div3.battles&account_id={accountId}";

            var jsonText = await Http.GetStringAsync(url, ct);
            var node = JsonNode.Parse(jsonText);
            if (node?["status"]?.ToString() != "ok") return info;

            var data = node["data"]?[accountId.ToString()] as JsonObject;
            if (data == null) return info;

            // 隐藏战绩
            if (data["hidden_profile"]?.ToString() == "true")
            {
                info.SearchHit = true;
                info.Battles = -1;
                return info;
            }

            var pvp = data["statistics"]?["pvp"] as JsonObject;
            if (pvp != null)
            {
                info.Battles = pvp["battles"]?.GetValue<int>() ?? 0;
                var wins = pvp["wins"]?.GetValue<double>() ?? 0;
                info.WinRate = info.Battles > 0 ? wins / info.Battles * 100 : 0;
            }

            // PR 近似: WG 不直接提供 PR，用胜率代替
            info.PrValue = (int)info.WinRate;
            info.PrName = info.WinRate switch
            {
                >= 58 => "优秀",
                >= 50 => "良好",
                >= 45 => "一般",
                _ => "较弱"
            };

            // WG API 不提供 avgDamage 和 avgFrags 的底层接口输出，保持默认
            info.AvgDamage = 0;
            info.AvgFrags = 0;
            info.Kd = 0;

            return info;
        }
        catch (OperationCanceledException)
        {
            info.HasError = true;
            info.ErrorMessage = "超时";
            return info;
        }
        catch (Exception ex)
        {
            info.HasError = true;
            info.ErrorMessage = ex.Message;
            return info;
        }
    }

    /// <summary>批量查询玩家战绩（并发 5，同 shinoaki 策略）。</summary>
    public static async Task<List<PlayerThreatInfo>> AssessPlayersAsync(
        List<PlayerShipPair> pairs, string server, string appId,
        bool useYuyukoProxy, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<PlayerThreatInfo>(pairs.Count);
        if (pairs.Count == 0) return results;

        // 1. 批量搜索所有玩家名
        progress?.Report("WG API: 搜索玩家账号中...");
        var playerNames = pairs.Select(p => p.Player).ToList();
        var nameToId = await SearchPlayersAsync(playerNames, server, appId, useYuyukoProxy, ct);
        progress?.Report($"WG API: 搜索完成，命中 {nameToId.Count}/{pairs.Count} 个玩家");

        // 2. 并发查询战绩
        using var gate = new SemaphoreSlim(5);
        var tasks = pairs.Select(async p =>
        {
            var name = p.Player?.Trim() ?? "";
            var hasColon = name.Contains(':');

            // 未搜到：可能是人机/隐藏/名字不匹配
            if (!nameToId.TryGetValue(name, out var accountId))
            {
                return new PlayerThreatInfo
                {
                    UserName = name,
                    ShipName = p.Ship,
                    Relation = p.Relation,
                    SearchHit = false,
                    HasColon = hasColon,
                };
            }

            await gate.WaitAsync(ct);
            try
            {
                var info = await GetPlayerInfoAsync(accountId, name, p.Ship, p.Relation, server, appId, useYuyukoProxy, ct);
                return info ?? new PlayerThreatInfo
                {
                    UserName = name,
                    ShipName = p.Ship,
                    Relation = p.Relation,
                    SearchHit = true,
                    HasColon = hasColon,
                    AccountId = accountId,
                };
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var total = tasks.Count;
        var done = 0;
        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks);
            tasks.Remove(finished);
            done++;
            var r = await finished;
            results.Add(r);
            progress?.Report($"WG API: 查询战绩中... {done}/{total}");
        }

        return results;
    }
}
