using System.IO;
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
        你是《战舰世界》(World of Warships) 资深战术助手，擅长结合阵容数据、小地图、战舰参数和玩家战绩做局势判断与威胁评估。

        【输入说明】
        - 阵容数据：可能来自自动检测（游戏文件解析，玩家名/舰船名/阵营 100%准确）或 AI 视觉识别（仅供参考，可能有误）。
          若为自动检测数据，阵营标签 [自己]/[队友]/[敌方] 是确定可信的，不需要额外验证。
        - 小地图截图（可能不提供）：图例 绿色=我方舰船，红色=敌方舰船，白色箭头=用户舰船。
        - 用户战舰名：用户自己驾驶的舰船。
        - 本局舰船列表：所有参战舰船（不分敌我）。
        - 战舰参数知识库：仅供内部参考，输出中不要复述、罗列参数。
        - 玩家威胁评估清单：由联网战绩查询得到，含每个玩家的阵营标签、搜索结果、PR/胜率/场均伤害/场均击杀/KD 等战绩。
          若为自动检测模式，玩家名与舰船名由游戏内部数据精确解析（100%准确），不需要验证。

        【严禁编造——最重要】
        - 战舰参数必须且只能来自知识库文本。任何具体数值必须能在知识库中找到出处，禁止凭印象给出数字。
        - 知识库未列出的项目，视为该舰"未提供/未知"。
        - 【消耗品一律禁提】知识库不包含消耗品数据。严禁提到任何消耗品名称或基于消耗品做战术建议。
        - 若某舰不在知识库中，明确说"参数未知"。

        【关键判断规则】
        1. 人机 vs 真人——综合以下信号判断：
           信号一·名字是否含冒号":"
           信号二·名字是否像无规律字母组合
           信号三·战绩搜索是否命中
           · 搜索命中 → 真人
           · 未命中+含冒号+字母无规律 → 人机
           · 未命中+中文/军团标签/用户_数字 → 真人
        2. 威胁评估：以清单中战绩数据为依据，结合舰船性能判断哪些真人最凶、最可能带节奏。
        3. 优先目标：综合战绩威胁度+舰船性能+是否真人确定优先处理目标。
        4. 若战绩查询失败，仅凭名字特征判断，标注"疑似"。

        【容错】
        - 小地图上可能没有敌方舰船（开局未点亮）：威胁与策略基于阵容和参数推断。
        - 双方可能出现同型舰：靠阵营标签区分。
        - 若阵容数据来自自动检测则阵营可信；若来自 AI 识别则以阵容图为准。

        【输出】直接给以下四部分，中文，分点，简洁：
        1.【怎么玩这艘船】针对用户战舰，结合参数特性给出本局打法要点。
        2.【敌方威胁评估】点明敌方人机/真人数量，对真人玩家结合战绩与舰船判断谁最凶。
        3.【优先攻击目标】明确建议优先处理的目标（具体舰船+理由+克制手段）。
        4.【整局局势与策略】结合小地图双方位置（若有）给出整体走向与关键决策。若敌方未点亮，给出开局预案。

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

            if (string.IsNullOrWhiteSpace(request.ImageBase64) && request.MinimapImage != null)
                request.ImageBase64 = ScreenCaptureService.EncodeToBase64(request.MinimapImage);
            if (request.LineupImage != null && string.IsNullOrWhiteSpace(request.LineupImageBase64))
                request.LineupImageBase64 = ScreenCaptureService.EncodeToBase64(request.LineupImage);

            if (string.IsNullOrWhiteSpace(request.ImageBase64))
                throw new InvalidOperationException("缺少小地图截图。");

            var systemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? DefaultSystemPrompt : request.SystemPrompt;
            var userText = BuildUserPrompt(request);

            var images = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.LineupImageBase64))
                images.Add(request.LineupImageBase64);
            images.Add(request.ImageBase64);

            var payload = BuildPayload(systemPrompt, userText, images.ToArray());
            var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions");
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            httpReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await HttpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errText = await resp.Content.ReadAsStringAsync(ct);
                result.Success = false;
                result.Error = $"{ProviderName} API 返回 {resp.StatusCode}: {Truncate(errText, 500)}";
                return result;
            }

            // 流式读取 SSE 响应
            var sb = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var chunkCallback = request.OnStreamChunk;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (line.Length == 0) continue;
                if (!line.StartsWith("data: ")) continue;

                var data = line[6..];
                if (data == "[DONE]") break;

                try
                {
                    var node = JsonNode.Parse(data);
                    var delta = node?["choices"]?[0]?["delta"]?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        sb.Append(delta);
                        chunkCallback?.Invoke(delta);
                    }
                }
                catch { /* 跳过解析异常的行 */ }
            }

            result.Success = true;
            result.Content = sb.ToString();
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

    /// <summary>构造用户提示词（含知识库、舰船列表、玩家威胁评估）</summary>
    protected virtual string BuildUserPrompt(BattleAnalysisRequest req)
    {
        var sb = new StringBuilder();

        if (req.LineupFromAutoDetect)
        {
            // 自动检测模式：数据来自游戏文件解析，100%准确
            sb.AppendLine("分析本局。阵营数据由游戏内部文件精确解析，无需验证。");
            sb.AppendLine("图片：小地图截图（绿=我方，红=敌方，白箭头=用户自己）");
        }
        else
        {
            // 手动识别模式（降级）：需要 AI 结合阵容图自行验证
            sb.AppendLine("分析本局。图片顺序：");
            if (!string.IsNullOrWhiteSpace(req.LineupImageBase64))
                sb.AppendLine("1) 阵容面板截图（请自行依据顶部「队友」/「敌方」绿色/红色标题判断敌我，不要假设左右分布）");
            sb.AppendLine("2) 小地图截图（绿=我方，红=敌方，白箭头=用户自己）");
        }
        sb.AppendLine();
        sb.AppendLine($"【我的战舰】{req.MyShip}");
        sb.AppendLine($"【本局所有舰船】{req.AllShips}" +
            (req.LineupFromAutoDetect ? "（来自游戏数据，100%准确）" : "（仅供参考，可能有误，请以阵容图为准）"));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(req.PlayerThreatText))
            sb.AppendLine(req.PlayerThreatText);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(req.KnowledgeBaseText))
            sb.AppendLine(req.KnowledgeBaseText);
        sb.AppendLine();
        if (req.LineupFromAutoDetect)
            sb.AppendLine("以上阵容数据和阵营标签由游戏文件精确解析（100%准确），请直接基于此进行分析。威胁评估参考上方玩家战绩清单。");
        else
            sb.AppendLine("请按系统提示要求，结合阵容图自行识别敌我、玩家名和舰船名，完成四部分输出。威胁评估参考上方玩家战绩清单，但如清单与图片不符以图片为准。");
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
            max_tokens = 2048,
            stream = true
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

        【阵容面板布局——最重要，必须严格遵守】
        阵容面板分左右两块，顶部各有一个标题：
        - 左侧标题是"队友"（绿色衬底/背景），下面是友方所有舰船
        - 右侧标题是"敌方"（红色衬底/背景），下面是敌方所有舰船
        每一行（无论左边还是右边）的结构完全相同，从左到右依次为：
        [玩家昵称] [飞船图标] [等级 舰船型号名]
        其中：
        - 玩家昵称可能带 [军团] 标签，如 "[北洋狮]xxx"，必须完整提取（方括号+军团名+昵称）
        - 飞船图标不用管
        - "等级 舰船型号名" 由【罗马数字等级前缀 + 空格 + 舰船名】组成，如 "X 大选帝侯"、"VII 沙恩霍斯特"
        注意：等级前缀永远在舰船名前面，中间用空格分隔，例如 "X 鲸" 是一个完整的舰船名，绝不能拆成 "X" 和 "鲸" 两个。

        【提取规则】
        1. 每一行提取一对 {player, ship}，player 为玩家昵称，ship 为"等级 舰船名"
        2. 严格按照面板从左到右、从上到下的顺序逐行扫描
        3. ship 字段格式必须是"罗马数字+空格+舰船名"，例如 "X 鲸"、"VIII 蒙大拿"
        4. player 字段包含 [军团] 标签和昵称，例如 "[LYG]桃夭汐汐"、"[MAC]中神战舰"
        5. 玩家名为 "用户_数字" 格式的也要完整提取（这是真人玩家的默认昵称）
        6. 不要修改、省略或编造任何字符，按图上原文提取
        7. 看不清的行直接跳过，不要猜测

        【什么是玩家名，什么是舰船名】
        - 玩家名特征：通常在每一行最左边，可能含 [军团] 标签、中文名、英文昵称、"用户_数字"格式
        - 舰船名特征：在玩家名右边，前面必有罗马数字等级前缀（I-XII），如 "III 无畏"、"IX 基尔萨奇"、"X 鲸"
        - 绝不能把"大选帝侯 X"（舰船名，等级在后被识别颠倒了）这种当成玩家名
        - 绝不能把"X 大选帝侯"（等级+舰船名）中的 X 和大选帝侯拆成两个

        【绝对禁止】
        - 禁止把等级前缀和舰船名拆开（"X 鲸" → 正确；"X" 和 "鲸" → 错误）
        - 禁止把舰船名当成玩家名
        - 禁止把玩家名当成舰船名
        - 禁止丢弃 [军团] 标签
        - 禁止修改任何字符

        严格只输出如下 JSON，不要任何额外文字、不要 Markdown 代码块标记：
        {"pairs":[{"player":"[LYG]桃夭汐汐","ship":"X 大选帝侯"},{"player":"[MAC]中神战舰","ship":"X 临海"}]}
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
            var userText = "请识别这张《战舰世界》开局阵容截图中每一行的玩家名与舰船名，组成配对返回，严格只输出 JSON。";
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

    /// <summary>从 AI 返回文本中提取配对列表（优先）或扁平舰船名列表（兼容）</summary>
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
