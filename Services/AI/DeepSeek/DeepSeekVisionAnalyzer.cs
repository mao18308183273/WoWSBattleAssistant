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

namespace WoWSBattleAssistant.Services.AI.DeepSeek;

/// <summary>
/// DeepSeek 视觉识图分析器。
/// 协议与 OpenAI 兼容协议不同:需上传文件拿 file_id + PoW 挑战 + SSE 流,
/// 故独立实现 <see cref="IAIBattleAnalyzer"/>,不继承 OpenAICompatibleAnalyzer。
///
/// 调用链:
/// 1) chat_session/create → chat_session_id
/// 2) create_pow_challenge(target_path=/api/v0/file/upload_file) → 挑战
/// 3) 解 PoW → x-ds-pow-response;upload_file(multipart) → file_id
/// 4) 轮询 fetch_files 直到 status=SUCCESS、audit_result=pass
/// 5) create_pow_challenge(target_path=/api/v0/chat/completion) → 挑战
/// 6) 解 PoW;completion(SSE) → 累积 RESPONSE 片段
/// </summary>
public sealed class DeepSeekVisionAnalyzer : IAIBattleAnalyzer
{
    private const string BaseHost = "https://chat.deepseek.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/150.0.0.0 Safari/537.36 Edg/150.0.0.0";
    private const string SecChUa = "\"Not;A=Brand\";v=\"8\", \"Chromium\";v=\"150\", \"Microsoft Edge\";v=\"150\"";

    private static readonly int TzOffset =
        (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds;

    private readonly string _token;
    private readonly string _cookie;
    private readonly bool _thinkingEnabled;

    private static readonly HttpClient Http = CreateClient();

    public string ProviderName => "DeepSeek 视觉";

    public DeepSeekVisionAnalyzer(string token, string cookie, bool thinkingEnabled = true)
    {
        _token = token ?? string.Empty;
        _cookie = cookie ?? string.Empty;
        _thinkingEnabled = thinkingEnabled;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false, // 手动带 Cookie 头,完全复刻抓包
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                | System.Net.DecompressionMethods.Deflate
                | System.Net.DecompressionMethods.Brotli,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        return c;
    }

    // ===== IAIBattleAnalyzer =====

    public async Task<BattleAnalysisResult> AnalyzeAsync(BattleAnalysisRequest request, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new BattleAnalysisResult { ProviderName = ProviderName };
        try
        {
            EnsureToken();

            if (request.MinimapImage != null && string.IsNullOrWhiteSpace(request.ImageBase64))
                request.ImageBase64 = ScreenCaptureService.EncodeToBase64(request.MinimapImage);
            if (request.LineupImage != null && string.IsNullOrWhiteSpace(request.LineupImageBase64))
                request.LineupImageBase64 = ScreenCaptureService.EncodeToBase64(request.LineupImage);

            if (string.IsNullOrWhiteSpace(request.ImageBase64))
                throw new InvalidOperationException("缺少小地图截图。");

            var systemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? DefaultSystemPrompt : request.SystemPrompt;
            var userText = BuildUserPrompt(request);
            var prompt = systemPrompt + "\n\n" + userText;

            // 收集图片(阵容在前,小地图在后)
            var images = new List<(byte[] bytes, string name)>();
            if (!string.IsNullOrWhiteSpace(request.LineupImageBase64))
                images.Add((Convert.FromBase64String(request.LineupImageBase64), "lineup.png"));
            images.Add((Convert.FromBase64String(request.ImageBase64), "minimap.png"));

            var content = await ChatWithImagesAsync(prompt, images, thinkingEnabled: _thinkingEnabled,
                request.OnStreamChunk, ct);
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

    public async Task<ShipRecognitionResult> RecognizeShipsAsync(BitmapSource lineupImage, CancellationToken ct = default)
    {
        var result = new ShipRecognitionResult { ProviderName = ProviderName, LineupImage = lineupImage };
        try
        {
            EnsureToken();
            if (lineupImage == null)
                throw new InvalidOperationException("缺少阵容截图。");

            var pngBytes = ScreenCaptureService.EncodeToPngBytes(lineupImage);
            var prompt = RecognitionSystemPrompt + "\n\n" +
                "请识别这张《战舰世界》开局阵容截图中每一行的玩家名与舰船名,组成配对返回,严格只输出 JSON。";

            var content = await ChatWithImagesAsync(prompt,
                new List<(byte[], string)> { (pngBytes, "lineup.png") },
                thinkingEnabled: false, onChunk: null, ct);

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

    // ===== 核心流程 =====

    /// <summary>上传图片并发起一次视觉对话,返回累积的 RESPONSE 正文。</summary>
    private async Task<string> ChatWithImagesAsync(string prompt, List<(byte[] bytes, string name)> images,
        bool thinkingEnabled, Action<string>? onChunk, CancellationToken ct)
    {
        // 1) 创建会话
        var sessionId = await CreateSessionAsync(ct);

        // 2~4) 逐张上传 + 轮询
        var fileIds = new List<string>();
        foreach (var img in images)
            fileIds.Add(await UploadAndConfirmAsync(img.bytes, img.name, thinkingEnabled, ct));

        // 5~6) completion 的 PoW + SSE
        await EnsureHifAsync(ct);
        var powHeader = await BuildPowHeaderAsync("/api/v0/chat/completion", ct);

        var body = new
        {
            chat_session_id = sessionId,
            parent_message_id = (string?)null,
            model_type = "vision",
            prompt,
            ref_file_ids = fileIds,
            thinking_enabled = thinkingEnabled,
            search_enabled = false,
            action = (string?)null,
            preempt = false,
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseHost}/api/v0/chat/completion");
        ApplyCommonHeaders(req);
        req.Headers.TryAddWithoutValidation("x-ds-pow-response", powHeader);
        req.Headers.TryAddWithoutValidation("x-hif-dliq", await GetHifAsync("dliq", ct));
        req.Headers.TryAddWithoutValidation("x-hif-leim", await GetHifAsync("leim", ct));
        req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errText = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"completion 请求失败 {resp.StatusCode}: {Truncate(errText, 500)}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await ReadSseAsync(reader, onChunk, ct);
    }

    /// <summary>POST /api/v0/chat_session/create → 返回 chat_session_id</summary>
    private async Task<string> CreateSessionAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseHost}/api/v0/chat_session/create");
        ApplyCommonHeaders(req);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var json = await SendAndReadAsync(req, ct);
        return json["data"]?["biz_data"]?["chat_session"]?["id"]?.ToString()
            ?? throw new InvalidOperationException("创建会话失败:未返回 chat_session_id。");
    }

    /// <summary>上传单张图片并轮询确认通过审核。</summary>
    private async Task<string> UploadAndConfirmAsync(byte[] pngBytes, string filename, bool thinkingEnabled, CancellationToken ct)
    {
        var powHeader = await BuildPowHeaderAsync("/api/v0/file/upload_file", ct);

        var fileContent = new ByteArrayContent(pngBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent
        {
            { fileContent, "file", filename }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseHost}/api/v0/file/upload_file");
        ApplyCommonHeaders(req);
        req.Headers.TryAddWithoutValidation("x-ds-pow-response", powHeader);
        req.Headers.TryAddWithoutValidation("x-model-type", "vision");
        req.Headers.TryAddWithoutValidation("x-file-size", pngBytes.Length.ToString());
        req.Headers.TryAddWithoutValidation("x-thinking-enabled", thinkingEnabled ? "1" : "0");
        req.Content = multipart;

        var json = await SendAndReadAsync(req, ct);
        var fileId = json["data"]?["biz_data"]?["id"]?.ToString()
            ?? throw new InvalidOperationException("上传文件失败:未返回 file_id。");

        await PollFileStatusAsync(fileId, ct);
        return fileId;
    }

    /// <summary>轮询 fetch_files 直到 SUCCESS 且 audit_result=pass。</summary>
    private async Task PollFileStatusAsync(string fileId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseHost}/api/v0/file/fetch_files?file_ids={Uri.EscapeDataString(fileId)}");
            ApplyCommonHeaders(req);
            var json = await SendAndReadAsync(req, ct);
            var file = json["data"]?["biz_data"]?["files"]?[0];
            if (file != null)
            {
                var status = file["status"]?.ToString();
                var audit = file["audit_result"]?.ToString();
                if (status == "SUCCESS")
                {
                    if (audit == "pass")
                        return;
                    if (audit == "block" || audit == "reviewing_fail")
                        throw new InvalidOperationException($"图片审核未通过(audit_result={audit})。");
                    // pass 之外的其他值继续轮询
                }
                else if (status == "FAILED")
                    throw new InvalidOperationException("文件处理失败。");
            }
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException("等待图片处理超时(60s)。");
    }

    // ===== PoW =====

    /// <summary>获取挑战并求解,返回 base64 编码的 x-ds-pow-response 头。</summary>
    private async Task<string> BuildPowHeaderAsync(string targetPath, CancellationToken ct)
    {
        var challenge = await GetPowChallengeAsync(targetPath, ct);
        var ch = challenge["challenge"]?.ToString()
            ?? throw new InvalidOperationException("PoW 挑战缺少 challenge 字段。");
        var salt = challenge["salt"]?.ToString()
            ?? throw new InvalidOperationException("PoW 挑战缺少 salt 字段。");
        var difficulty = challenge["difficulty"]?.GetValue<long>()
            ?? throw new InvalidOperationException("PoW 挑战缺少 difficulty 字段。");
        var expireAt = challenge["expire_at"]?.GetValue<long>()
            ?? throw new InvalidOperationException("PoW 挑战缺少 expire_at 字段。");
        var signature = challenge["signature"]?.ToString()
            ?? throw new InvalidOperationException("PoW 挑战缺少 signature 字段。");

        var answer = await DeepSeekPowSolver.SolveAsync(ch, salt, difficulty, expireAt, ct);

        var powObj = new
        {
            algorithm = "DeepSeekHashV1",
            challenge = ch,
            salt,
            answer,
            signature,
            target_path = targetPath,
        };
        var powJson = JsonSerializer.Serialize(powObj, JsonOpts);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(powJson));
    }

    /// <summary>POST /api/v0/chat/create_pow_challenge</summary>
    private async Task<JsonNode> GetPowChallengeAsync(string targetPath, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseHost}/api/v0/chat/create_pow_challenge");
        ApplyCommonHeaders(req);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { target_path = targetPath }), Encoding.UTF8, "application/json");
        var json = await SendAndReadAsync(req, ct);
        return json["data"]?["biz_data"]?["challenge"]
            ?? throw new InvalidOperationException("获取 PoW 挑战失败。");
    }

    // ===== hif 缓存 =====

    private static readonly object HifLock = new();
    private static (string value, DateTime expiry) _hifDliq = ("", DateTime.MinValue);
    private static (string value, DateTime expiry) _hifLeim = ("", DateTime.MinValue);

    /// <summary>过期则刷新两个 hif 令牌。</summary>
    private static async Task EnsureHifAsync(CancellationToken ct)
    {
        await GetHifAsync("dliq", ct);
        await GetHifAsync("leim", ct);
    }

    private static async Task<string> GetHifAsync(string kind, CancellationToken ct)
    {
        (string value, DateTime expiry) cur;
        lock (HifLock)
        {
            cur = kind == "dliq" ? _hifDliq : _hifLeim;
        }
        if (!string.IsNullOrEmpty(cur.value) && cur.expiry > DateTime.UtcNow)
            return cur.value;

        var url = $"https://hif-{kind}.deepseek.com/query";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.ParseAdd("*/*");
        req.Headers.TryAddWithoutValidation("sec-ch-ua", SecChUa);
        req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        req.Headers.Referrer = new Uri("https://chat.deepseek.com/");
        req.Headers.Add("Origin", "https://chat.deepseek.com");

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"获取 hif-{kind} 失败: {resp.StatusCode}");
        var json = await resp.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(json);
        var value = node?["data"]?["biz_data"]?["value"]?.ToString()
            ?? throw new InvalidOperationException($"hif-{kind} 响应缺少 value。");

        // 默认 600s,留 30s 余量提前刷新
        var ttl = 600;
        if (resp.Headers.TryGetValues("x-hif-ttl", out var ttls))
        {
            var first = ttls.FirstOrDefault();
            if (first != null && int.TryParse(first, out var t) && t > 0) ttl = t;
        }
        var expiry = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(30, ttl - 30));

        lock (HifLock)
        {
            if (kind == "dliq") _hifDliq = (value, expiry);
            else _hifLeim = (value, expiry);
        }
        return value;
    }

    // ===== SSE 解析 =====

    /// <summary>读取 SSE 流,累积 RESPONSE 类型片段的正文并返回。</summary>
    private static async Task<string> ReadSseAsync(StreamReader reader, Action<string>? onChunk, CancellationToken ct)
    {
        var fragments = new List<Fragment>();
        var finished = false;
        var lastRespLen = 0; // 跟踪上次 RESPONSE 总长度，用于计算增量

        while (!finished)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (line.Length == 0) continue;
            if (line[0] == ':') continue;

            if (line.StartsWith("event:"))
            {
                var ev = line["event:".Length..].Trim();
                if (ev == "close") finished = true;
                continue;
            }

            if (!line.StartsWith("data:")) continue;
            var payload = line["data:".Length..].TrimStart();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            finished |= ProcessSseData(payload, fragments);

            // 流式回调：每次处理 SSE 数据后，报告新增的 RESPONSE 内容
            if (onChunk != null)
            {
                var currentResp = new StringBuilder();
                foreach (var f in fragments)
                    if (f.Type == "RESPONSE")
                        currentResp.Append(f.Content);
                var currentLen = currentResp.Length;
                if (currentLen > lastRespLen)
                {
                    var delta = currentResp.ToString(lastRespLen, currentLen - lastRespLen);
                    onChunk(delta);
                    lastRespLen = currentLen;
                }
            }
        }

        var sb = new StringBuilder();
        foreach (var f in fragments)
        {
            if (f.Type == "RESPONSE")
                sb.Append(f.Content);
        }
        var text = sb.ToString();
        return string.IsNullOrEmpty(text)
            ? "（DeepSeek 未返回正文,可能已截断。思考链: " + string.Concat(fragments.Where(f => f.Type == "THINK").Select(f => f.Content)) + ")"
            : text;
    }

    /// <summary>处理一条 SSE data,返回 true 表示收到完成信号。</summary>
    private static bool ProcessSseData(string payload, List<Fragment> fragments)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(payload); }
        catch { return false; }
        if (node == null) return false;

        // 初始完整响应:{"v":{"response":{...,"fragments":[...]}}}
        if (node["v"] is JsonObject vObj && vObj["response"] is JsonObject respObj)
        {
            if (respObj["fragments"] is JsonArray arr)
            {
                foreach (var f in arr)
                    AddFragment(fragments, f);
            }
            var st = respObj["status"]?.ToString();
            return st == "FINISHED";
        }

        var p = node["p"]?.ToString();
        var o = node["o"]?.ToString();
        var val = node["v"];

        if (p == null)
        {
            // 纯 {"v":"片段"} → 追加到当前最后一个片段
            if (val is JsonValue jv && fragments.Count > 0)
                fragments[^1].Content.Append(jv.ToString());
            return false;
        }

        // 批量操作
        if (p == "response" && o == "BATCH" && val is JsonArray batch)
        {
            bool done = false;
            foreach (var item in batch)
            {
                if (item?["p"]?.ToString() == "response/status" && item["v"]?.ToString() == "FINISHED")
                    done = true;
                if (item?["p"]?.ToString() == "quasi_status" && item["v"]?.ToString() == "FINISHED")
                    done = true;
            }
            return done;
        }

        // 新增片段
        if (p == "response/fragments" && o == "APPEND" && val is JsonArray newFrags)
        {
            foreach (var f in newFrags)
                AddFragment(fragments, f);
            return false;
        }

        // 片段正文追加(无论 o 是否给出,content 默认按追加处理)
        if (p.EndsWith("/content", StringComparison.Ordinal))
        {
            if (fragments.Count > 0 && val != null)
                fragments[^1].Content.Append(val.ToString());
            return false;
        }

        // 完成信号
        if (p == "response/status" && val?.ToString() == "FINISHED")
            return true;

        return false;
    }

    private static void AddFragment(List<Fragment> fragments, JsonNode? f)
    {
        if (f == null) return;
        var type = f["type"]?.ToString() ?? "RESPONSE";
        var content = f["content"]?.ToString() ?? "";
        fragments.Add(new Fragment { Type = type, Content = new StringBuilder(content) });
    }

    private sealed class Fragment
    {
        public string Type { get; set; } = "";
        public StringBuilder Content { get; set; } = new();
    }

    // ===== HTTP 通用 =====

    /// <summary>给请求加上通用反检测头(authorization/cookie/client/浏览器指纹)。</summary>
    private void ApplyCommonHeaders(HttpRequestMessage req)
    {
        req.Headers.Accept.ParseAdd("*/*");
        req.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8,en-GB;q=0.7,en-US;q=0.6");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (!string.IsNullOrWhiteSpace(_cookie))
            req.Headers.TryAddWithoutValidation("Cookie", _cookie);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.TryAddWithoutValidation("sec-ch-ua", SecChUa);
        req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
        req.Headers.Add("Origin", "https://chat.deepseek.com");
        req.Headers.Referrer = new Uri("https://chat.deepseek.com/");
        req.Headers.TryAddWithoutValidation("x-client-version", "2.3.0");
        req.Headers.TryAddWithoutValidation("x-client-platform", "web");
        req.Headers.TryAddWithoutValidation("x-client-bundle-id", "com.deepseek.chat");
        req.Headers.TryAddWithoutValidation("x-client-locale", "zh_CN");
        req.Headers.TryAddWithoutValidation("x-client-timezone-offset", TzOffset.ToString());
    }

    /// <summary>发送请求,读取并校验业务码,返回根 JsonNode。</summary>
    private static async Task<JsonNode> SendAndReadAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"{req.RequestUri?.AbsolutePath} 请求失败 {resp.StatusCode}: {Truncate(text, 500)}");

        var node = JsonNode.Parse(text)
            ?? throw new InvalidOperationException($"{req.RequestUri?.AbsolutePath} 响应不是合法 JSON。");
        var code = node["code"]?.GetValue<int>();
        if (code != 0)
        {
            var msg = node["msg"]?.ToString() ?? node["data"]?["biz_msg"]?.ToString() ?? "";
            throw new InvalidOperationException($"{req.RequestUri?.AbsolutePath} 业务失败 code={code}: {msg}");
        }
        return node;
    }

    private void EnsureToken()
    {
        if (string.IsNullOrWhiteSpace(_token))
            throw new InvalidOperationException("未配置 DeepSeek Token,请在设置中填写。");
    }

    // ===== 提示词(与 OpenAICompatibleAnalyzer 保持一致) =====

    private const string DefaultSystemPrompt =
        """
        你是《战舰世界》(World of Warships) 资深战术助手，擅长结合阵容数据、小地图、战舰参数和玩家战绩做局势判断与威胁评估。

        【输入说明】
        - 阵容数据：可能来自自动检测（游戏文件解析，玩家名/舰船名/阵营 100%准确）或 AI 视觉识别（仅供参考）。
          若为自动检测数据，阵营标签 [自己]/[队友]/[敌方] 是确定可信的。
        - 小地图截图：图例 绿色=我方舰船，红色=敌方舰船，白色箭头=用户舰船。
        - 用户战舰名：用户自己驾驶的舰船。
        - 本局舰船列表：所有参战舰船（不分敌我）。
        - 战舰参数知识库：仅供内部参考，输出中不要复述、罗列参数。
        - 玩家威胁评估清单：由联网战绩查询得到，含阵营标签、搜索结果、战绩数据。

        【严禁编造——最重要】
        - 战舰参数必须且只能来自知识库文本。禁止凭印象给出数字。
        - 【消耗品一律禁提】知识库不包含消耗品数据。
        - 若某舰不在知识库中，明确说"参数未知"。

        【关键判断规则】
        1. 人机 vs 真人——综合名字特征+战绩搜索命中判断。
        2. 威胁评估：以战绩数据为依据，结合舰船性能判断。
        3. 优先目标：综合战绩威胁度+舰船性能+是否真人确定。

        【输出】四部分，中文，分点，简洁：
        1.【怎么玩这艘船】针对用户战舰给出打法要点。
        2.【敌方威胁评估】点明敌军人机/真人数量与威胁排序。
        3.【优先攻击目标】具体舰船+理由+克制手段。
        4.【整局局势与策略】结合小地图给出整体走向。

        所有建议必须落到本局具体舰船和位置上，禁止通用套话。
        """;

    private const string RecognitionSystemPrompt =
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

    /// <summary>构造用户提示词(与基类一致，支持自动检测/手动识别双模式)。</summary>
    private static string BuildUserPrompt(BattleAnalysisRequest req)
    {
        var sb = new StringBuilder();

        if (req.LineupFromAutoDetect)
        {
            sb.AppendLine("分析本局。阵营数据由游戏内部文件精确解析，无需验证。");
            sb.AppendLine("图片：小地图截图（绿=我方，红=敌方，白箭头=用户自己）");
        }
        else
        {
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

    /// <summary>从 AI 返回文本中提取配对列表(优先)或扁平舰船名列表(兼容)。</summary>
    private static void ParseLineupJson(string content, ShipRecognitionResult result)
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
