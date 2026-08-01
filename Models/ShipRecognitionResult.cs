using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace WoWSBattleAssistant.Models;

/// <summary>
/// AI 识别阵容图的结果。
/// 注意：阵容图里无法直接区分"用户自己的船"，所以 my 留空，
/// 我方全部填入 Allies，UI 让用户从中指定一艘作为 MyShip。
/// </summary>
public sealed class ShipRecognitionResult
{
    public bool Success { get; set; }
    public List<string> Allies { get; set; } = new();
    public List<string> Enemies { get; set; } = new();
    public string? Error { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public BitmapSource? LineupImage { get; set; }
    public string RawContent { get; set; } = string.Empty;
}
