using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WoWSBattleAssistant.Models;

namespace WoWSBattleAssistant.Services.Shinoaki;

/// <summary>
/// wows.mgaia.top 背后的 shinoaki 公开 API 客户端。
/// 认证为静态公钥（Authorization: WEB_API:wows_yuyuko），无需登录态。
/// 用于：①按玩家名搜索判断真/人机 ②拉取真人玩家战绩供 AI 威胁评估。
/// </summary>
public sealed class ShinoakiApiClient
{
    private const string BaseUrl = "https://v3-api.wows.shinoaki.com";
    private const string AuthHeader = "WEB_API:wows_yuyuko";
    private const string ClientType = "WEB;0.0.0";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// 按玩家名搜索。返回首个匹配的 accountId；搜不到或异常返回 null。
    /// 搜索为精确匹配真实玩家名：搜得到=真人，搜不到=人机/不存在。
    /// </summary>
    public static async Task<long?> SearchPlayerAsync(string userName, string server,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;

        var url = $"{BaseUrl}/public/wows/account/search/{server}/user";
        var body = JsonSerializer.Serialize(new { userName, server, limit = 1 });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Authorization", AuthHeader);
            req.Headers.TryAddWithoutValidation("Yuyuko-Client-Type", ClientType);
            req.Headers.TryAddWithoutValidation("Origin", "https://wows.mgaia.top");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var node = JsonNode.Parse(json);
            var code = node?["code"]?.GetValue<int>() ?? 0;
            if (code != 200) return null; // 404=不存在, 502=上游异常, 均视为搜不到

            var arr = node?["data"] as JsonArray;
            var first = arr?.FirstOrDefault() as JsonObject;
            var id = first?["accountId"]?.GetValue<long>();
            return id;
        }
        catch
        {
            return null; // 网络/超时/解析异常，降级为搜不到
        }
    }

    /// <summary>
    /// 拉取玩家信息并提取威胁评估关键字段。
    /// 失败时返回的 PlayerThreatInfo.HasError=true，不抛异常。
    /// </summary>
    public static async Task<PlayerThreatInfo> GetPlayerInfoAsync(long accountId, string server,
        string userName, string shipName, CancellationToken ct = default)
    {
        var info = new PlayerThreatInfo
        {
            UserName = userName,
            ShipName = shipName,
            IsRealPlayer = true,
            AccountId = accountId,
            JudgeReason = "搜索命中"
        };

        try
        {
            var url = $"{BaseUrl}/public/wows/account/user/info?accountId={accountId}&server={server}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", AuthHeader);
            req.Headers.TryAddWithoutValidation("Yuyuko-Client-Type", ClientType);
            req.Headers.TryAddWithoutValidation("Origin", "https://wows.mgaia.top");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var node = JsonNode.Parse(json);
            var code = node?["code"]?.GetValue<int>() ?? 0;
            if (code != 200)
            {
                info.HasError = true;
                info.ErrorMessage = $"user/info code={code}";
                return info;
            }

            var data = node?["data"] as JsonObject;
            if (data == null)
            {
                info.HasError = true;
                info.ErrorMessage = "data 为空";
                return info;
            }

            // PR 总评
            var prInfo = data["prInfo"] as JsonObject;
            info.PrValue = prInfo?["value"]?.GetValue<int>() ?? 0;
            info.PrName = prInfo?["name"]?.ToString() ?? "";

            // PVP 战斗类型下的 shipInfo
            var battleType = data["battleTypeInfo"] as JsonObject;
            var pvp = battleType?["PVP"] as JsonObject;
            var shipInfo = pvp?["shipInfo"] as JsonObject;
            var battleInfo = shipInfo?["battleInfo"] as JsonObject;
            var avgInfo = shipInfo?["avgInfo"] as JsonObject;

            info.Battles = battleInfo?["battle"]?.GetValue<int>() ?? 0;
            info.WinRate = avgInfo?["win"]?.GetValue<double>() ?? 0;
            info.AvgDamage = avgInfo?["damage"]?.GetValue<int>() ?? 0;
            info.AvgFrags = avgInfo?["frags"]?.GetValue<double>() ?? 0;
            info.Kd = avgInfo?["kd"]?.GetValue<double>() ?? 0;
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

    /// <summary>
    /// 批量评估一组"玩家名+舰船名"配对的真人/人机身份与战绩。
    /// 判定规则：
    ///   1. 玩家名含冒号':' → 人机（不查网络，快速判断）
    ///   2. 否则搜索 shinoaki：搜到=真人并查战绩；搜不到=人机
    /// 并发受限（5）避免限流；单个失败不影响整体。
    /// </summary>
    public static async Task<List<PlayerThreatInfo>> AssessPlayersAsync(
        List<PlayerShipPair> pairs, string server, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<PlayerThreatInfo>(pairs.Count);
        if (pairs.Count == 0) return results;

        using var gate = new SemaphoreSlim(5);
        var tasks = pairs.Select(async p =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await AssessOneAsync(p, server, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        // 逐个完成时上报进度
        var total = tasks.Count;
        var done = 0;
        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            done++;
            var r = await finished.ConfigureAwait(false);
            results.Add(r);
            progress?.Report($"查询玩家战绩中... {done}/{total}");
        }
        return results;
    }

    private static async Task<PlayerThreatInfo> AssessOneAsync(PlayerShipPair pair, string server,
        CancellationToken ct)
    {
        var name = pair.Player?.Trim() ?? "";
        var ship = pair.Ship?.Trim() ?? "";

        // 1. 冒号判断：名字带冒号的是人机
        if (name.Contains(':'))
        {
            return new PlayerThreatInfo
            {
                UserName = name,
                ShipName = ship,
                IsRealPlayer = false,
                JudgeReason = "玩家名含冒号"
            };
        }

        // 2. 搜索判断真/人机
        var accountId = await SearchPlayerAsync(name, server, ct).ConfigureAwait(false);
        if (accountId == null)
        {
            return new PlayerThreatInfo
            {
                UserName = name,
                ShipName = ship,
                IsRealPlayer = false,
                JudgeReason = "shinoaki 搜索未命中"
            };
        }

        // 3. 真人 → 查战绩
        return await GetPlayerInfoAsync(accountId.Value, server, name, ship, ct).ConfigureAwait(false);
    }
}
