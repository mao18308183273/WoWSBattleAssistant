using System.Text.Json.Nodes;

namespace WoWSBattleAssistant.Models;

/// <summary>
/// 单个玩家的威胁评估信息。
/// 来源：阵容识别（玩家名+舰船名）→ shinoaki 搜索（判真人/人机）→ user/info（提取战绩）。
/// </summary>
public sealed class PlayerThreatInfo
{
    /// <summary>玩家名（阵容图中识别到的原名）</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>该玩家所驾驶的舰船名</summary>
    public string ShipName { get; set; } = string.Empty;

    /// <summary>是否判定为真人玩家（false=人机）</summary>
    public bool IsRealPlayer { get; set; }

    /// <summary>判定依据说明，如"冒号"、"搜索命中"、"搜索未命中"</summary>
    public string JudgeReason { get; set; } = string.Empty;

    /// <summary>真人玩家的 accountId（人机为 0）</summary>
    public long AccountId { get; set; }

    // ===== 战绩字段（仅真人玩家有值）=====
    public int PrValue { get; set; }
    public string PrName { get; set; } = string.Empty;
    public int Battles { get; set; }
    public double WinRate { get; set; }
    public int AvgDamage { get; set; }
    public double AvgFrags { get; set; }
    public double Kd { get; set; }

    /// <summary>查询是否出错（网络/超时/解析失败）。出错时不影响主流程，降级为"未知"。</summary>
    public bool HasError { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>格式化为给 AI 看的紧凑一行文本</summary>
    public string ToAiLine()
    {
        if (!IsRealPlayer)
            return $"  - {UserName}（{ShipName}）: 人机（{JudgeReason}）";

        if (HasError)
            return $"  - {UserName}（{ShipName}）: 真人，战绩查询失败（{ErrorMessage}）";

        return $"  - {UserName}（{ShipName}）: 真人，PR {PrValue}({PrName})，" +
               $"{Battles}场，胜率{WinRate:0.0}%，场均伤害{AvgDamage}，场均击杀{AvgFrags:0.0}，KD{Kd:0.00}";
    }
}

/// <summary>阵容识别的"玩家名+舰船名"配对</summary>
public sealed class PlayerShipPair
{
    public string Player { get; set; } = string.Empty;
    public string Ship { get; set; } = string.Empty;
}
