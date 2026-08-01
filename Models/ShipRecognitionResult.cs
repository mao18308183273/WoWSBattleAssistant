using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace WoWSBattleAssistant.Models;

/// <summary>
/// AI 识别阵容图的结果（扁平舰船名列表，不分敌我）。
/// 敌我分队交给分析阶段由 AI 看阵容图自行判断——
/// 因为不同游戏模式（随机/排位/行动）阵容面板的左右分布不同，本地硬分会出错。
/// </summary>
public sealed class ShipRecognitionResult
{
    public bool Success { get; set; }
    /// <summary>识别到的所有舰船名（不分阵营）</summary>
    public List<string> Ships { get; set; } = new();
    public string? Error { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public BitmapSource? LineupImage { get; set; }
    public string RawContent { get; set; } = string.Empty;
}
