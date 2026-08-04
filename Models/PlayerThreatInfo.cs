using System.Text.Json.Nodes;

namespace WoWSBattleAssistant.Models;

/// <summary>
/// 单个玩家的威胁评估信息。
/// 来源：tempArenaInfo.json 自动解析（玩家名/shipId/阵营）→ shinoaki/WG API 战绩查询。
/// 玩家名与舰船名由本地文件解析（100%准确），不再依赖 AI 视觉识别。
/// 最终真人/人机判断由 AI 综合规则完成。
/// </summary>
public sealed class PlayerThreatInfo
{
    /// <summary>玩家名（来自 tempArenaInfo.json，含 [军团] 标签）</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>该玩家所驾驶的舰船名（中文，来自本地知识库 shipId 映射）</summary>
    public string ShipName { get; set; } = string.Empty;

    /// <summary>阵营: 0=自己, 1=队友, 2=敌方</summary>
    public int Relation { get; set; }

    /// <summary>shinoaki/WG 搜索是否命中（true=搜到, false=未搜到, null=查询出错）</summary>
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
        var sideTag = Relation switch { 0 => "自己", 1 => "队友", 2 => "敌方", _ => "未知" };

        if (HasError)
            return $"  - [{sideTag}] {UserName}（{ShipName}）: 战绩查询失败（{ErrorMessage}），{colonTag}";

        if (SearchHit == true)
        {
            return $"  - [{sideTag}] {UserName}（{ShipName}）: 战绩搜索命中，PR {PrValue}({PrName})，" +
                   $"{Battles}场，胜率{WinRate:0.0}%，场均伤害{AvgDamage}，场均击杀{AvgFrags:0.0}，KD{Kd:0.00}，{colonTag}";
        }

        if (SearchHit == false)
            return $"  - [{sideTag}] {UserName}（{ShipName}）: 战绩搜索未命中，{colonTag}";

        return $"  - [{sideTag}] {UserName}（{ShipName}）: 战绩未查询，{colonTag}";
    }
}

/// <summary>阵容中的"玩家名+舰船名+阵营"配对（来自 tempArenaInfo.json 自动解析）</summary>
public sealed class PlayerShipPair
{
    public string Player { get; set; } = string.Empty;
    public string Ship { get; set; } = string.Empty;
    /// <summary>阵营: 0=自己, 1=队友, 2=敌方</summary>
    public int Relation { get; set; }
}
