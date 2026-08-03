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
        你是《战舰世界》(World of Warships) 资深战术助手，擅长结合阵容面板、小地图、战舰参数和玩家战绩做局势判断与威胁评估。

        【输入】
        - 阵容面板截图：含双方"玩家名+舰船名"。请你自行看图判断敌我——用户战舰所在一方为我方（辅助判断方法提供的截图顶部会有“队友”和“敌方”两种标识：1. 只有队友：下面全是队友。2. 有敌方：下面就是敌人。“队友”是绿色衬底，“敌方”是红色衬底，在绿色和红色衬底下面，才是双方的人和战舰。），不要假设左右分布（随机/排位/行动等模式阵容面板左右不同）。
        - 小地图截图：图例 绿色=我方舰船，红色=敌方舰船，白色箭头=用户自己的舰船。
        - 用户战舰名 + 本局所有舰船名（扁平列表，可能含重复：双方同型舰会出现两次）。
        - 战舰参数知识库：仅供你内部参考，输出中不要复述、罗列参数。
        - 玩家威胁评估清单：由联网查询 shinoaki 接口得到，提供每个玩家的搜索结果（命中/未命中）、玩家名是否含冒号、以及命中玩家的 PR/胜率/场均伤害/场均击杀/KD 等战绩。清单不做人机判定，需要你综合判断。

        【严禁编造——最重要】
        - 战舰参数（射程、隐蔽、伤害、航速、装甲等）必须且只能来自上方知识库文本。任何具体数值（如"射程18.6km""隐蔽5.8km""装填30秒"）必须能在知识库文本中找到出处，禁止凭印象或常识给出数字。
        - 知识库未列出的项目，视为该舰"未提供/未知"，绝不可凭印象或常识编造。
        - 【消耗品一律禁提】战舰知识库根本不包含消耗品数据（烟幕/引擎增压/雷达/水听/维修/发烟机/加速等），你没有任何依据判断某舰是否有某消耗品。一律当作"未知"：
          · 严禁在输出中提到任何消耗品名称
          · 严禁基于消耗品做战术建议（如"等他雷达结束再上""躲烟幕后""对水听范围外机动"）
          · 改用基于舰船参数的描述代替（如"利用岛屿掩护接近""保持在隐蔽距离外""利用高航速转场"）
          · 搞错消耗品会导致严重误判，这条是硬禁区
        - 若某舰不在知识库中，明确说"参数未知"，不要编造任何数值。

        【关键判断规则】
        1. 人机 vs 真人——由你综合以下三个信号判断，不要单凭任何一个信号下定论：
           信号一·名字是否含冒号":"：人机玩家名通常带冒号（如 ":AI:xxx"），真人玩家名一般不带。但极少数真人名字也可能带冒号，不能单凭冒号定人机。
           信号二·英文字母组合是否像人机：人机玩家的名字通常是无规律的英文字母组合（如 "BtrkXz"、"qwRfm"），而真人玩家的英文名通常有意义（单词、缩写或混拼）。中文名、带[军团]标签的名、"用户_数字"格式的名都是真人。
           信号三·shinoaki 搜索是否命中：清单中标注了每个玩家"shinoaki搜索命中"或"shinoaki搜索未命中"。搜索命中=该玩家在战绩网站有记录，几乎可以确定是真人。搜索未命中可能是人机，但也可能是真人（名字识别有偏差、玩家未注册战绩等），需要结合信号一和信号二综合判断。
           综合规则：
           · 搜索命中 → 确定真人，可直接使用其战绩数据
           · 搜索未命中 + 名字含冒号 + 字母组合无规律 → 判定为人机
           · 搜索未命中 + 名字含中文/军团标签/用户_数字 → 判定为真人（搜索未命中可能是名字识别偏差）
           · 搜索未命中 + 英文名但像有意义的单词 → 倾向真人但标注"疑似"
           · 搜索未命中 + 英文名且无规律 + 不含冒号 → 倾向人机但标注"疑似"
        2. 威胁评估：对判定为真人的玩家，以清单中战绩数据为依据——PR 值越高越强（参考：<900 较弱, 900-1450 中等, 1450-2100 很好, >2100 优秀），胜率与场均伤害反映玩家水平与战舰发挥。结合舰船性能综合判断哪几个真人最凶、最可能带节奏。人机玩家普遍威胁较低，但所驾舰船性能仍要考虑（如人机开 BB 仍有火力威胁）。
        3. 优先目标：综合"玩家战绩威胁度""舰船性能威胁""是否真人"确定本局应优先处理的目标。
        4. 若清单缺失或某玩家战绩查询失败（查询失败会在清单中标注），仅凭信号一和信号二判断，且必须明确标注"疑似"而非断言。

        【容错】
        - 小地图上可能没有敌方舰船（开局对面未点亮）：此时威胁与策略基于阵容和参数推断，不要编造敌方位置。
        - 双方可能出现同型舰：靠阵容图中的阵营归属区分，不要混淆敌我同型舰。
        - 若阵容图里某些信息看不清，按能看清的部分判断，不要瞎编。

        【输出】直接给以下四部分，中文，分点，简洁。可引用具体数值（如"隐蔽5.8km""主炮射程18km"）但不要整段抄参数，不要废话套话：
        1.【怎么玩这艘船】针对用户战舰，结合其参数特性给出本局打法要点：接敌距离、走位思路、应避免的对抗等。注意：不要提任何消耗品（知识库无此数据），改用基于舰船参数的描述。
        2.【敌方威胁评估】
           - 先点明敌方有几艘是人机、几艘是真人（由你根据上述三条规则综合判断，并简要说明判断依据）。
           - 对真人玩家，结合其战绩（PR/胜率/场均伤害）与所驾舰船判断谁最凶、最可能带节奏，说明理由。
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

    /// <summary>构造用户提示词（含知识库、扁平舰船列表、玩家威胁评估，不分敌我）</summary>
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
        if (!string.IsNullOrWhiteSpace(req.PlayerThreatText))
            sb.AppendLine(req.PlayerThreatText);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(req.KnowledgeBaseText))
            sb.AppendLine(req.KnowledgeBaseText);
        sb.AppendLine();
        sb.AppendLine("按系统提示的四部分输出，不要复述参数。威胁评估以上方玩家战绩清单为准。");
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
        阵容面板中每一行包含"玩家名"和其驾驶的"舰船名"两部分，你的任务是把它们分别提取出来组成配对。

        【什么是舰船名，什么是玩家名——必须分清】
        - 舰船名（填到 ship 字段）：游戏内显示的战舰型号名称，由"罗马数字等级前缀 + 空格 + 舰船型号名"组成。
          你必须完整保留等级前缀，格式为"等级+空格+舰船名"，因为同名的舰船可能存在于不同等级（如"无畏"既有IX级也有III级），
          等级前缀是区分重名舰船的关键信息。
          · 正确示例：图上显示 "VII 沙恩霍斯特" → ship 字段填 "VII 沙恩霍斯特"
          · 正确示例：图上显示 "X 大和" → ship 字段填 "X 大和"
          · 正确示例：图上显示 "VIII 蒙大拿" → ship 字段填 "VIII 蒙大拿"
          · 错误示例：ship 字段只填 "沙恩霍斯特"（丢失了等级前缀，重名时无法区分）
        - 玩家名（填到 player 字段）：玩家账号昵称，常见形式有：
          · 带 [军团] 标签的真人玩家，如 "[北洋狮]xxx" —— 方括号内的"北洋狮"是军团名，方括号外的"xxx"是玩家昵称，两者合起来才是完整玩家名，必须整体填入 player 字段，不可拆分、不可丢弃方括号部分
          · 不带军团标签的真人玩家昵称（中文或英文），包括 "用户_123456" 这种默认昵称（注册账号未改名的真人玩家，是真人不是人机）
          · 人机玩家的名字通常带冒号 ":" 且为无规律英文字母（如 ":AI:xxx"）—— 冒号原样保留在 player 字段里

        【识别要求】
        1. 每行提取玩家名和舰船名组成配对。ship 字段必须包含等级前缀，格式为"罗马数字+空格+舰船名"。
        2. 玩家名要尽量完整准确——后续会用它联网查询玩家战绩，错一个字就查不到，宁可照搬图上原文也不要改写。
        3. [军团] 标签是玩家名的一部分，必须包含在 player 字段里。
        4. 玩家名中的冒号 ":" 原样保留（后续代码会用它做人机判定的辅助信号）。
        5. 不要区分敌我/左右阵营，阵营判断由后续阶段处理。
        6. 同名舰船若出现多次（双方都有同型舰），按出现次数重复列出，各自对应其玩家名。
        7. 若某行看不清，宁可省略也不要编造。

        【绝对禁止】
        - ship 字段必须包含等级前缀，不可省略（这是区分重名舰船的关键）
        - 不要把玩家名（含 [军团] 标签、冒号、用户_数字 等）当成舰船名填到 ship 字段
        - 不要把舰船名当成玩家名填到 player 字段
        - 不要丢掉玩家名中的 [军团] 标签
        - 不要给玩家名加游戏中不存在的字符

        严格只输出如下 JSON，不要任何额外文字、不要 Markdown 代码块标记：
        {"pairs":[{"player":"玩家名1","ship":"VII 舰船名1"},{"player":"玩家名2","ship":"X 舰船名2"}]}
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
