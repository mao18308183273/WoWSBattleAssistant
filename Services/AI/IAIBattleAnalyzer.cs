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
        你是《战舰世界》(World of Warships) 资深战术助手，擅长结合阵容面板、小地图、战舰参数和玩家信息做局势判断与威胁评估。

        【输入】
        - 阵容面板截图：含双方"玩家名+舰船名"。请你自行看图判断敌我——用户战舰所在一方为我方，不要假设左右分布（随机/排位/行动等模式阵容面板左右不同）。
        - 小地图截图：图例 绿色=我方舰船，红色=敌方舰船，白色箭头=用户自己的舰船。
        - 用户战舰名 + 本局所有舰船名（扁平列表，可能含重复：双方同型舰会出现两次）。
        - 战舰参数知识库：仅供你内部参考，输出中不要复述、罗列参数。

        【严禁编造——最重要】
        - 战舰参数（射程、隐蔽、伤害、航速、装甲、消耗品等）必须且只能来自上方知识库。
        - 知识库未列出的项目，视为该舰"未提供/未知"，绝不可凭印象或常识编造。
        - 尤其消耗品（烟幕/引擎增压/雷达/水听/维修等）：知识库未列出就当作"未知"，禁止猜测某舰有某消耗品。搞错消耗品会导致严重误判。
        - 若某舰不在知识库中，明确说"参数未知"，不要编造任何数值。

        【关键判断规则】
        1. 人机 vs 真人：看阵容图中玩家名——名字里带冒号":"的是人机(AI)，没有冒号的是真人玩家。
        2. 威胁评估：真人玩家通常比人机更危险、更可能带节奏；结合玩家名风格与所驾舰船性能综合判断哪几个真人最凶。
        3. 优先目标：综合"舰船威胁度"与"是否真人"确定本局应优先处理的目标。

        【容错】
        - 小地图上可能没有敌方舰船（开局对面未点亮）：此时威胁与策略基于阵容和参数推断，不要编造敌方位置。
        - 双方可能出现同型舰：靠阵容图中的阵营归属区分，不要混淆敌我同型舰。
        - 若阵容图里某些信息看不清，按能看清的部分判断，不要瞎编。

        【输出】直接给以下四部分，中文，分点，简洁。可引用具体数值（如"隐蔽5.8km""主炮射程18km"）但不要整段抄参数，不要废话套话：
        1.【怎么玩这艘船】针对用户战舰，结合其参数特性给出本局打法要点：接敌距离、走位思路、消耗品时机、应避免的对抗。
        2.【敌方威胁评估】
           - 先点明敌方有几艘是人机、几艘是真人（依据玩家名冒号）。
           - 对真人玩家，结合其玩家名与舰船判断谁最凶、最可能带节奏，说明理由。
           - 结合舰船性能（主炮口径/射程/隐蔽/鱼雷/机动/防空）说明每个重点目标的威胁点。
        3.【优先攻击目标】明确给出本局建议优先处理的目标（具体舰船+是否真人），一句话理由+克制手段。
        4.【整局局势与策略】结合小地图双方位置分布（若有红色敌舰）给出整体走向与关键决策（推进/转场/控点/视野/集火）。若敌方未点亮，给出开局预案。

        所有建议必须落到本局具体舰船和位置上，禁止与局势无关的通用套话。
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

            // 编码两张图
            if (string.IsNullOrWhiteSpace(request.ImageBase64) && request.MinimapImage != null)
                request.ImageBase64 = ScreenCaptureService.EncodeToBase64(request.MinimapImage);
            if (request.LineupImage != null && string.IsNullOrWhiteSpace(request.LineupImageBase64))
                request.LineupImageBase64 = ScreenCaptureService.EncodeToBase64(request.LineupImage);

            if (string.IsNullOrWhiteSpace(request.ImageBase64))
                throw new InvalidOperationException("缺少小地图截图。");

            var systemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? DefaultSystemPrompt : request.SystemPrompt;

            var userText = BuildUserPrompt(request);

            // 收集所有要发送的图片（阵容图在前，小地图在后）
            var images = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.LineupImageBase64))
                images.Add(request.LineupImageBase64);
            images.Add(request.ImageBase64);

            var payload = BuildPayload(systemPrompt, userText, images.ToArray());
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

    /// <summary>构造用户提示词（含知识库与扁平舰船列表，不分敌我）</summary>
    protected virtual string BuildUserPrompt(BattleAnalysisRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine("分析本局。图片顺序：");
        if (!string.IsNullOrWhiteSpace(req.LineupImageBase64))
            sb.AppendLine("1) 阵容面板截图（自己判断敌我，用户战舰所在一方为我方）");
        sb.AppendLine("2) 小地图截图（绿=我方，红=敌方，白箭头=用户自己）");
        sb.AppendLine();
        sb.AppendLine($"【我的战舰】{req.MyShip}");
        sb.AppendLine($"【本局所有舰船】{req.AllShips}（含重复=双方同型舰）");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(req.KnowledgeBaseText))
            sb.AppendLine(req.KnowledgeBaseText);
        sb.AppendLine();
        sb.AppendLine("按系统提示的三部分输出，不要复述参数。");
        return sb.ToString();
    }

    /// <summary>构造 OpenAI 兼容的请求体（支持多张图片）</summary>
    protected virtual string BuildPayload(string systemPrompt, string userText, params string[] imageBase64List)
    {
        var contentList = new List<object>();
        foreach (var img in imageBase64List)
        {
            contentList.Add(new { type = "image_url", image_url = new { url = $"data:image/png;base64,{img}" } });
        }
        contentList.Add(new { type = "text", text = userText });

        var payload = new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = contentList }
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
        阵容面板包含两方所有舰船的名称。

        识别要求：
        1. 提取图中出现的每一艘战舰的名称（游戏内显示的舰船名，如"大和""蒙大拿""Z-52"等）。
        2. 不要区分敌我/左右阵营，只返回一个扁平的舰船名列表。阵营判断由后续分析阶段另行处理。
        3. 同名舰船若出现多次（双方都有同型舰），按出现次数重复列出。
        4. 若某项识别不确定，宁可省略也不要编造。

        严格只输出如下 JSON，不要任何额外文字、不要 Markdown 代码块标记：
        {"ships":["舰船名1","舰船名2","舰船名3"]}
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
            var userText = "请识别这张《战舰世界》开局阵容截图中的所有舰船名，不分阵营返回扁平列表，严格只输出 JSON。";
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

    /// <summary>从 AI 返回文本中提取 ships JSON 扁平列表</summary>
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
            if (node?["ships"] is JsonArray arr)
                result.Ships = arr.Select(x => x?.ToString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

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
