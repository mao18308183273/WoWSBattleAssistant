using System.Windows.Media.Imaging;

namespace WoWSBattleAssistant.Models;

/// <summary>一次战局分析的输入</summary>
public sealed class BattleAnalysisRequest
{
    /// <summary>小地图截图</summary>
    public BitmapSource MinimapImage { get; set; } = null!;

    /// <summary>用户自己的战舰名称</summary>
    public string MyShip { get; set; } = string.Empty;

    /// <summary>我方其他战舰名称（逗号或顿号分隔）</summary>
    public string AlliedShips { get; set; } = string.Empty;

    /// <summary>敌方战舰名称（逗号或顿号分隔）</summary>
    public string EnemyShips { get; set; } = string.Empty;

    /// <summary>从知识库预提取的相关战舰参数文本</summary>
    public string KnowledgeBaseText { get; set; } = string.Empty;

    /// <summary>系统提示词（空则用各 Analyzer 内置默认）</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>截图的 Base64（PNG）</summary>
    public string ImageBase64 { get; set; } = string.Empty;
}

/// <summary>分析结果</summary>
public sealed class BattleAnalysisResult
{
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Error { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
}
