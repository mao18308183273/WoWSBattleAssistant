using System.Windows.Media.Imaging;

namespace WoWSBattleAssistant.Models;

/// <summary>一次战局分析的输入</summary>
public sealed class BattleAnalysisRequest
{
    /// <summary>小地图截图</summary>
    public BitmapSource MinimapImage { get; set; } = null!;

    /// <summary>开局阵容面板截图（可选，用于 AI 验证。自动检测模式下可能为 null）</summary>
    public BitmapSource? LineupImage { get; set; }

    /// <summary>用户自己的战舰名称</summary>
    public string MyShip { get; set; } = string.Empty;

    /// <summary>本局所有舰船名称（扁平列表，不分敌我）</summary>
    public string AllShips { get; set; } = string.Empty;

    /// <summary>从知识库预提取的相关战舰参数文本</summary>
    public string KnowledgeBaseText { get; set; } = string.Empty;

    /// <summary>玩家威胁评估文本（来自战绩 API 查询：阵营+搜索命中+战绩数据）。
    /// 由 MainWindow 在分析前注入，AI 据此而非"看玩家名风格"判断威胁。</summary>
    public string PlayerThreatText { get; set; } = string.Empty;

    /// <summary>系统提示词（空则用各 Analyzer 内置默认）</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>小地图截图的 Base64（PNG）</summary>
    public string ImageBase64 { get; set; } = string.Empty;

    /// <summary>阵容截图的 Base64（PNG）。自动检测模式下可能为空。</summary>
    public string LineupImageBase64 { get; set; } = string.Empty;

    /// <summary>阵容是否来自自动检测（true=tempArenaInfo.json 解析，100%准确无需AI验证）。</summary>
    public bool LineupFromAutoDetect { get; set; }

    /// <summary>流式输出回调。AI 每生成一段文本就调用一次，用于实时更新 UI。</summary>
    public Action<string>? OnStreamChunk { get; set; }
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
