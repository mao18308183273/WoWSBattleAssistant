using System.Windows;

namespace WoWSBattleAssistant.Models;

/// <summary>
/// 应用配置。持久化到 %AppData%\WoWSBattleAssistant\settings.json
/// </summary>
public sealed class AppSettings
{
    /// <summary>当前选用的 AI 提供方</summary>
    public AiProvider AiProvider { get; set; } = AiProvider.Glm;

    // ===== 智谱 GLM =====
    public string GlmApiKey { get; set; } = string.Empty;
    public string GlmModel { get; set; } = "glm-4v"; // glm-4v / glm-4v-plus

    // ===== 通义千问 VL =====
    public string QwenApiKey { get; set; } = string.Empty;
    public string QwenModel { get; set; } = "qwen-vl-plus"; // qwen-vl-plus / qwen-vl-max

    // ===== DeepSeek 视觉(网页版逆向,需登录态) =====
    /// <summary>authorization Bearer Token,从浏览器 F12 → Network → 任意 api 请求的 authorization 头复制(去掉 "Bearer " 前缀)</summary>
    public string DeepSeekToken { get; set; } = string.Empty;
    /// <summary>Cookie 整行(含 ds_session_id 等),从 F12 → Network → 请求头 cookie 复制。用于通过 WAF。</summary>
    public string DeepSeekCookie { get; set; } = string.Empty;

    /// <summary>DeepSeek 思考模式（开启=显示思考链但更慢，关闭=仅回复文本更快）</summary>
    public bool EnableDeepSeekThinking { get; set; } = true;

    /// <summary>战舰数据 JSON 文件路径（945 艘船知识库）</summary>
    public string ShipDataPath { get; set; } =
        @"C:\Users\mao_z\Downloads\wows_ships_data_20260801_125351.json";

    /// <summary>游戏服务器（用于 shinoaki 玩家战绩查询）。cn=国服, asia=亚服, eu=欧服, na=美服, ru=俄服</summary>
    public string Server { get; set; } = "cn";

    /// <summary>游戏安装目录（用于自动读取 tempArenaInfo.json）。留空则自动从注册表检测。</summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>是否启用阵容自动检测（读取 tempArenaInfo.json，跳过截图+AI识别步骤）</summary>
    public bool AutoDetectLineup { get; set; } = true;

    /// <summary>战绩查询 API 后端选择</summary>
    public ApiBackend ApiBackend { get; set; } = ApiBackend.Shinoaki;

    /// <summary>WG Public API 的 application_id（公开 ID，用于战绩查询）</summary>
    public string WgApplicationId { get; set; } = "447ec579e994976e39dec0e7d0bac644";

    /// <summary>小地图在屏幕上的区域（设备像素坐标）</summary>
    public Rect MinimapRegion { get; set; } = Rect.Empty;

    /// <summary>悬浮窗位置 X（逻辑像素）</summary>
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;
    public double WindowWidth { get; set; } = 420;
    public double WindowHeight { get; set; } = 620;

    /// <summary>分析时是否附加完整战舰知识库文本（默认仅附加命中的舰船）</summary>
    public bool AttachKnowledgeBase { get; set; } = true;

    /// <summary>自定义系统提示词（可空，空则用内置默认）</summary>
    public string SystemPrompt { get; set; } = string.Empty;
}

public enum AiProvider
{
    Glm,        // 智谱 GLM-4V / GLM-4V-Plus
    Qwen,       // 阿里通义千问 VL
    DeepSeek    // DeepSeek 网页版视觉(逆向,需登录态 Token+Cookie)
}

/// <summary>战绩查询 API 后端</summary>
public enum ApiBackend
{
    Shinoaki,       // wows.mgaia.top 背后 shinoaki API（默认，当前使用）
    WgPublic,       // WG Public API（官方，不支持 RU/CN）
    Vortex,         // Vortex API（第三方，支持所有区服）
    WgPublicYuyuko  // WG Public API 经 Yuyuko 中国代理
}
