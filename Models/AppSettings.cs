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

    /// <summary>战舰数据 JSON 文件路径（945 艘船知识库）</summary>
    public string ShipDataPath { get; set; } =
        @"C:\Users\mao_z\Downloads\wows_ships_data_20260801_125351.json";

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
    Glm,    // 智谱 GLM-4V / GLM-4V-Plus
    Qwen    // 阿里通义千问 VL
}
