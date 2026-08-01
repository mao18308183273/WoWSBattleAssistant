using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using WoWSBattleAssistant.Models;

namespace WoWSBattleAssistant.Services.AI;

/// <summary>AI 战局分析统一接口</summary>
public interface IAIBattleAnalyzer
{
    string ProviderName { get; }
    Task<BattleAnalysisResult> AnalyzeAsync(BattleAnalysisRequest request, CancellationToken ct = default);

    /// <summary>
    /// 识别开局读秒阶段的双方阵容截图，返回我方/敌方舰船名列表。
    /// 阵容图无法直接区分"用户自己的船"，故我方全部返回到 Allies，由 UI 让用户指定。
    /// </summary>
    Task<ShipRecognitionResult> RecognizeShipsAsync(BitmapSource lineupImage, CancellationToken ct = default);
}

/// <summary>
/// OpenAI 兼容协议的基类。智谱 GLM-4V 与通义千问 VL 都提供 OpenAI 兼容端点，
/// 区别仅在 base URL、模型名、API Key。
/// </summary>
public abstract class OpenAICompatibleAnalyzer : IAIBattleAnalyzer
{
    protected abstract string BaseUrl { get; }
    protected abstract string Model { get; }
    protected abstract string ApiKey { get; }
    public abstract string ProviderName { get; }

    protected virtual string DefaultSystemPrompt =>
        """
        你是《战舰世界》(World of Warships) 的资深战术分析助手。用户会提供：
        1. 一张小地图截图（显示双方舰船当前位置）
        2. 用户自己的战舰名称
        3. 我方其他战舰名称
        4. 敌方战舰名称
        5. 这些战舰的官方参数数据（作为知识库）

        请基于小地图位置关系和各舰船参数特性，输出三部分：
        【敌方威胁分析】指出敌方最危险的舰船（基于主炮口径/射程/隐蔽/鱼雷等），说明需重点防范的目标。
        【走位建议】结合小地图当前位置和我方战舰参数，给出具体走位方向、交战距离、推进/撤退/转场建议。
        【本局玩法提示】利用岛屿、控制视野、鱼雷预射、开局/中盘/残局的注意事项。

        要求：中文输出，分点说明，简洁有重点，结合具体数值（如"XX隐蔽好需警惕"、"XX主炮射程18km可远距离消耗"），避免空话。
        """;

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(3) };

    public async Task<BattleAnalysisResult> AnalyzeAsync(BattleAnalysisRequest request, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new BattleAnalysisResult { ProviderName = ProviderName };
        try
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
                throw new InvalidOperationException($"未配置 {ProviderName} 的 API Key，请在设置中填写。");
            if (string.IsNullOrWhiteSpace(request.ImageBase64) && request.MinimapImage != null)
                request.ImageBase64 = ScreenCaptureService.EncodeToBase64(request.MinimapImage);
            if (string.IsNullOrWhiteSpace(request.ImageBase64))
                throw new InvalidOperationException("缺少小地图截图。");

            var systemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? DefaultSystemPrompt : request.SystemPrompt;

            var userText = BuildUserPrompt(request);

            var payload = BuildPayload(systemPrompt, userText, request.ImageBase64);
            var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions");
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            httpReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await HttpClient.SendAsync(httpReq, ct);
            var respJson = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                result.Success = false;
                result.Error = $"{ProviderName} API 返回 {resp.StatusCode}: {Truncate(respJson, 500)}";
                return result;
            }

            var content = ParseContent(respJson);
            result.Success = true;
            result.Content = content;
            result.Elapsed = sw.Elapsed;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "已取消。";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.Elapsed = sw.Elapsed;
            return result;
        }
    }

    /// <summary>构造用户提示词（含知识库与阵容）</summary>
    protected virtual string BuildUserPrompt(BattleAnalysisRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine("请分析这局《战舰世界》战局（小地图截图见图片）：");
        sb.AppendLine();
        sb.AppendLine($"【我的战舰】{req.MyShip}");
        sb.AppendLine($"【我方其他战舰】{req.AlliedShips}");
        sb.AppendLine($"【敌方战舰】{req.EnemyShips}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(req.KnowledgeBaseText))
        {
            sb.AppendLine(req.KnowledgeBaseText);
        }
        sb.AppendLine();
        sb.AppendLine("请根据小地图上各舰船的位置（颜色/图标分布）以及上述参数，给出战术分析。");
        return sb.ToString();
    }

    /// <summary>构造 OpenAI 兼容的请求体</summary>
    protected virtual string BuildPayload(string systemPrompt, string userText, string imageBase64)
    {
        var payload = new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = $"data:image/png;base64,{imageBase64}" } },
                        new { type = "text", text = userText }
                    }
                }
            },
            temperature = 0.6,
            max_tokens = 2048
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>从响应里解析出助手回复文本（兼容 OpenAI 与部分厂商变体）</summary>
    protected virtual string ParseContent(string respJson)
    {
        var node = JsonNode.Parse(respJson);
        var content = node?["choices"]?[0]?["message"]?["content"]?.ToString();
        if (string.IsNullOrEmpty(content))
        {
            // 某些厂商可能把内容放在别的字段
            content = node?["choices"]?[0]?["message"]?["content_text"]?.ToString();
        }
        return content ?? $"（{ProviderName} 未返回内容）响应: {Truncate(respJson, 300)}";
    }

    protected static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "...";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ===== 阵容识别 =====

    /// <summary>识别阵容图的系统提示词</summary>
    protected virtual string RecognitionSystemPrompt =>
        """
        你是《战舰世界》(World of Warships) 的图像识别助手。用户会提供一张游戏开局读秒阶段的双方阵容面板截图。
        阵容面板通常一边是我方（盟友）舰船列表，另一边是敌方舰船列表，请根据图中的布局/颜色/分组识别。

        识别要求：
        1. 提取每一艘战舰的名称（游戏内显示的舰船名，如"大和""蒙大拿""Z-52"等）。
        2. 分为"allies"（我方）和"enemies"（敌方）两组。
        3. 阵容图无法直接区分玩家自己的船，请把所有我方舰船都放进 allies 数组，不要猜测哪艘是玩家本人。
        4. 若某项识别不确定，宁可省略也不要编造。

        严格只输出如下 JSON，不要任何额外文字、不要 Markdown 代码块标记：
        {"allies":["舰船名1","舰船名2"],"enemies":["舰船名1","舰船名2"]}
        """;

    public async Task<ShipRecognitionResult> RecognizeShipsAsync(BitmapSource lineupImage, CancellationToken ct = default)
    {
        var result = new ShipRecognitionResult { ProviderName = ProviderName, LineupImage = lineupImage };
        try
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
                throw new InvalidOperationException($"未配置 {ProviderName} 的 API Key，请在设置中填写。");
            if (lineupImage == null)
                throw new InvalidOperationException("缺少阵容截图。");

            var imageBase64 = ScreenCaptureService.EncodeToBase64(lineupImage);
            var userText = "请识别这张《战舰世界》开局阵容截图中的舰船名，按我方/敌方分组，严格只输出 JSON。";
            var payload = BuildPayload(RecognitionSystemPrompt, userText, imageBase64);

            var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions");
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            httpReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await HttpClient.SendAsync(httpReq, ct);
            var respJson = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                result.Success = false;
                result.Error = $"{ProviderName} API 返回 {resp.StatusCode}: {Truncate(respJson, 500)}";
                return result;
            }

            var content = ParseContent(respJson);
            result.RawContent = content;
            ParseLineupJson(content, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "已取消。";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            return result;
        }
    }

    /// <summary>从 AI 返回文本中提取 allies/enemies JSON</summary>
    private static void ParseLineupJson(string content, ShipRecognitionResult result)
    {
        // 去掉可能的 ```json ... ``` 包裹
        var text = content.Trim();
        var fenceMatch = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success) text = fenceMatch.Groups[1].Value.Trim();

        // 找到第一个 { 到最后一个 }
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first >= 0 && last > first)
            text = text.Substring(first, last - first + 1);

        try
        {
            var node = JsonNode.Parse(text);
            var allies = node?["allies"];
            var enemies = node?["enemies"];
            if (allies is JsonArray arrA)
                result.Allies = arrA.Select(x => x?.ToString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
            if (enemies is JsonArray arrE)
                result.Enemies = arrE.Select(x => x?.ToString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

            result.Success = result.Allies.Count > 0 || result.Enemies.Count > 0;
            if (!result.Success)
                result.Error = "AI 未识别到任何舰船名，请重试或手动输入。";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = "解析 AI 返回 JSON 失败: " + ex.Message + " | 原文: " + Truncate(content, 300);
        }
    }
}

/// <summary>智谱 GLM-4V / GLM-4V-Plus</summary>
public sealed class GlmBattleAnalyzer : OpenAICompatibleAnalyzer
{
    private readonly string _apiKey;
    private readonly string _model;

    public GlmBattleAnalyzer(string apiKey, string model)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "glm-4v" : model;
    }

    protected override string BaseUrl => "https://open.bigmodel.cn/api/paas/v4";
    protected override string Model => _model;
    protected override string ApiKey => _apiKey;
    public override string ProviderName => $"智谱 {_model}";
}

/// <summary>阿里通义千问 VL（OpenAI 兼容模式）</summary>
public sealed class QwenVlBattleAnalyzer : OpenAICompatibleAnalyzer
{
    private readonly string _apiKey;
    private readonly string _model;

    public QwenVlBattleAnalyzer(string apiKey, string model)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen-vl-plus" : model;
    }

    protected override string BaseUrl => "https://dashscope.aliyuncs.com/compatible-mode/v1";
    protected override string Model => _model;
    protected override string ApiKey => _apiKey;
    public override string ProviderName => $"通义 {_model}";
}
