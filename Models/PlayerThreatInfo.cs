using System.Text.Json.Nodes;

namespace WoWSBattleAssistant.Models;

/// <summary>
/// 单个玩家的威胁评估信息。
/// 来源：阵容识别（玩家名+舰船名）→ shinoaki 搜索（提供搜索结果供 AI 判断）→ user/info（提取战绩）。
/// 注意：本类不做人机判定，只提供原始数据（搜索是否命中、是否含冒号、战绩），
/// 最终真人/人机判断由 AI 综合三条规则完成。
/// </summary>
public sealed class PlayerThreatInfo
{
    /// <summary>玩家名（阵容图中识别到的原名）</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>该玩家所驾驶的舰船名</summary>
    public string ShipName { get; set; } = string.Empty;

    /// <summary>shinoaki 搜索是否命中（true=搜到, false=未搜到, null=查询出错）</summary>
    public bool? SearchHit { get; set; }

    /// <summary>玩家名是否含冒号":"（人机特征之一）</summary>
    public bool HasColon { get; set; }

    /// <summary>真人玩家的 accountId（未命中时为 0）</summary>
    public long AccountId { get; set; }

    // ===== 战绩字段（仅搜索命中时有值）=====
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

    /// <summary>格式化为给 AI 看的紧凑一行文本（只提供数据，不做判定）</summary>
    public string ToAiLine()
    {
        var colonTag = HasColon ? "名字含冒号" : "名字不含冒号";

        if (HasError)
            return $"  - {UserName}（{ShipName}）: shinoaki查询失败（{ErrorMessage}），{colonTag}";

        if (SearchHit == true)
        {
            return $"  - {UserName}（{ShipName}）: shinoaki搜索命中，PR {PrValue}({PrName})，" +
                   $"{Battles}场，胜率{WinRate:0.0}%，场均伤害{AvgDamage}，场均击杀{AvgFrags:0.0}，KD{Kd:0.00}，{colonTag}";
        }

        if (SearchHit == false)
            return $"  - {UserName}（{ShipName}）: shinoaki搜索未命中，{colonTag}";

        return $"  - {UserName}（{ShipName}）: shinoaki未查询，{colonTag}";
    }
}

/// <summary>阵容识别的"玩家名+舰船名"配对</summary>
public sealed class PlayerShipPair
{
    public string Player { get; set; } = string.Empty;
    public string Ship { get; set; } = string.Empty;
}
