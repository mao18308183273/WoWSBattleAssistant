using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace WoWSBattleAssistant.Models;

/// <summary>
/// AI 识别阵容图的结果。
/// Ships：扁平舰船名列表（向后兼容手动修正流程）。
/// PlayerShipPairs：玩家名+舰船名配对（用于后续 shinoaki 搜索判真人/人机与战绩查询）。
/// 敌我分队交给分析阶段由 AI 看阵容图自行判断——
/// 因为不同游戏模式（随机/排位/行动）阵容面板的左右分布不同，本地硬分会出错。
/// </summary>
public sealed class ShipRecognitionResult
{
    public bool Success { get; set; }
    /// <summary>识别到的所有舰船名（不分阵营）</summary>
    public List<string> Ships { get; set; } = new();
    /// <summary>玩家名+舰船名配对（识别失败或解析失败时为空，降级用 Ships）</summary>
    public List<PlayerShipPair> PlayerShipPairs { get; set; } = new();
    public string? Error { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public BitmapSource? LineupImage { get; set; }
    public string RawContent { get; set; } = string.Empty;
}
