using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 战舰数据知识库。加载 945 艘船的 JSON，按名称索引，
/// 并提供"按船名精简提取关键参数"的方法，避免把 33MB 数据全量塞给 AI。
/// </summary>
public sealed class ShipDatabase
{
    /// <summary>船名(原始) -> 船的 JsonNode</summary>
    private readonly Dictionary<string, JsonObject> _byName = new();

    /// <summary>"vlevel name"（如 "VII 沙恩霍斯特"）-> 船的 JsonNode</summary>
    private readonly Dictionary<string, JsonObject> _byVlevelName = new();

    /// <summary>小写船名 -> 原始船名（用于大小写不敏感匹配）</summary>
    private readonly Dictionary<string, string> _lowerToName = new();

    /// <summary>shipId (游戏内数字ID) -> 船的 JsonNode（用于 tempArenaInfo.json 匹配）</summary>
    private readonly Dictionary<long, JsonObject> _byShipId = new();

    private int _totalCount;
    private bool _loaded;

    public int TotalCount => _totalCount;
    public bool IsLoaded => _loaded;

    /// <summary>加载 JSON 文件并建立索引。</summary>
    public async Task LoadAsync(string path, IProgress<string>? progress = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"战舰数据文件不存在: {path}");

        progress?.Report("正在读取战舰数据文件...");
        var bytes = await File.ReadAllBytesAsync(path);
        var json = JsonNode.Parse(bytes);
        if (json is not JsonArray arr)
            throw new InvalidDataException("战舰数据文件不是 JSON 数组");

        _byName.Clear();
        _byVlevelName.Clear();
        _lowerToName.Clear();
        _byShipId.Clear();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var name = obj["name"]?.ToString().Trim();
            if (string.IsNullOrEmpty(name)) continue;
            _byName[name] = obj;
            _lowerToName[name.ToLowerInvariant()] = name;

            // 建立 shipId 索引（用于 tempArenaInfo.json 的 shipId 映射）
            if (obj["ship_id"]?.GetValue<long>() is long sid && sid > 0)
                _byShipId[sid] = obj;

            // 同时建立 "vlevel name" 索引（如 "VII 沙恩霍斯特"），用于区分重名舰船
            var vlevel = obj["vlevel"]?.ToString().Trim();
            if (!string.IsNullOrEmpty(vlevel))
                _byVlevelName[$"{vlevel} {name}"] = obj;
        }
        progress?.Report($"已加载 {_byName.Count} 艘战舰，{_byShipId.Count} 个 shipId 索引");
        _totalCount = _byName.Count;
        _loaded = true;
        progress?.Report($"已加载 {_totalCount} 艘战舰数据");
    }

    /// <summary>按名称查找（支持等级前缀精确匹配、大小写不敏感、去空格、部分匹配）</summary>
    public JsonObject? TryGetShip(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();

        // 0. 如果含等级前缀（如 "VII 沙恩霍斯特"），优先按 vlevel+name 精确匹配
        if (_byVlevelName.TryGetValue(n, out var vlExact)) return vlExact;

        // 0.1 大小写不敏感的 vlevel+name 匹配
        var lowerVl = n.ToLowerInvariant();
        foreach (var kv in _byVlevelName)
        {
            if (kv.Key.ToLowerInvariant() == lowerVl) return kv.Value;
        }

        // 1. 纯名精确
        if (_byName.TryGetValue(n, out var exact)) return exact;

        // 2. 大小写不敏感
        if (_lowerToName.TryGetValue(n.ToLowerInvariant(), out var origName))
            return _byName[origName];

        // 3. 包含匹配（用户输入可能是别名或带前后缀）
        var lower = n.ToLowerInvariant();
        var hit = _lowerToName.FirstOrDefault(kv => kv.Key.Contains(lower) || lower.Contains(kv.Key));
        if (hit.Value != null) return _byName[hit.Value];

        return null;
    }

    /// <summary>按游戏内 shipId 查找舰船数据</summary>
    public JsonObject? TryGetByShipId(long shipId)
        => _byShipId.TryGetValue(shipId, out var obj) ? obj : null;

    /// <summary>按游戏内 shipId 获取显示名称（"vlevel name" 格式，如 "X 大选帝侯"）。未命中返回 "未知舰船(shipId)"。</summary>
    public string GetShipDisplayName(long shipId)
    {
        if (_byShipId.TryGetValue(shipId, out var obj))
        {
            var name = obj["name"]?.ToString()?.Trim() ?? "";
            var vlevel = obj["vlevel"]?.ToString()?.Trim() ?? "";
            return string.IsNullOrEmpty(vlevel) ? name : $"{vlevel} {name}";
        }
        return $"未知舰船({shipId})";
    }

    /// <summary>按游戏内 shipId 获取舰船等级(tier)。未命中返回 0。</summary>
    public int GetShipTier(long shipId)
    {
        if (_byShipId.TryGetValue(shipId, out var obj))
            return obj["tier"]?.GetValue<int>() ?? 0;
        return 0;
    }

    /// <summary>按游戏内 shipId 获取舰船类型（战列舰/巡洋舰/驱逐舰/航母/潜艇）。未命中返回 "未知"。</summary>
    public string GetShipType(long shipId)
    {
        if (_byShipId.TryGetValue(shipId, out var obj))
            return obj["vtype"]?.ToString() ?? "未知";
        return "未知";
    }

    /// <summary>
    /// 根据多艘船名生成精简的知识库文本。未命中的船名也会列出（提示 AI 数据缺失）。
    /// </summary>
    public string BuildKnowledgeText(IEnumerable<string> shipNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 战舰参数知识库（来自游戏官方数据）===");
        var seen = new HashSet<string>();
        var missed = new List<string>();

        foreach (var rawName in shipNames)
        {
            if (string.IsNullOrWhiteSpace(rawName)) continue;
            var name = rawName.Trim();
            if (!seen.Add(name.ToLowerInvariant())) continue; // 去重

            var ship = TryGetShip(name);
            if (ship == null)
            {
                missed.Add(name);
                continue;
            }
            sb.AppendLine(BuildShipSummary(ship));
        }

        if (missed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【未在知识库中找到的舰船，请基于常识分析】:" + string.Join("、", missed));
        }
        return sb.ToString();
    }

    /// <summary>把单艘船的关键参数格式化为紧凑文本（约 300-500 字符）</summary>
    public string BuildShipSummary(JsonObject ship)
    {
        var sb = new StringBuilder();
        var name = ship["name"]?.ToString() ?? "?";
        var tier = ship["tier"]?.ToString() ?? "?";
        var nation = ship["nation"]?.ToString() ?? "?";
        var vtype = ship["vtype"]?.ToString() ?? "?";
        var nationName = NationMap.TryGetValue(nation, out var nn) ? nn : nation;

        var tags = new List<string>();
        if (ship["is_premium"]?.GetValue<bool>() == true) tags.Add("金币");
        if (ship["is_special"]?.GetValue<bool>() == true) tags.Add("特种");
        var tagStr = tags.Count > 0 ? $" [{string.Join("/", tags)}]" : "";

        sb.Append($"【{name}】{nationName} {vtype} {tier}级{tagStr}");

        // 收集所有分项参数为 key->string 字典
        var p = CollectParams(ship);

        // 主炮
        if (p.TryGetValue("artillery_name", out var artName))
            sb.Append($"\n  主炮: {artName} {p.GetOrEmpty("artillery_slots")}".TrimEnd());
        if (p.TryGetValue("artillery_shot_delay", out var reload))
            sb.Append($" | 装填{reload}s");
        if (p.TryGetValue("artillery_distance", out var range))
            sb.Append($" | 射程{range}km");
        if (p.TryGetValue("artillery_max_dispersion", out var disp))
            sb.Append($" | 偏差{disp}m");
        if (p.TryGetValue("artillery_rotation_time", out var rot))
            sb.Append($" | 回转180°{rot}s");

        // 炮弹伤害
        if (p.TryGetValue("artillery_AP_damage", out var apDmg))
            sb.Append($"\n  AP弹: {apDmg}伤害");
        if (p.TryGetValue("artillery_AP_bullet_speed", out var apSpd))
            sb.Append($" {apSpd}m/s");
        if (p.TryGetValue("artillery_HE_damage", out var heDmg))
            sb.Append($" | HE弹: {heDmg}伤害");
        if (p.TryGetValue("artillery_HE_burn_probability", out var heBurn))
            sb.Append($" 起火{heBurn}%");

        // 鱼雷（torpedoes 分项，仅有配置与装填）
        if (p.TryGetValue("torpedoes_slots", out var torpSlots))
        {
            sb.Append($"\n  鱼雷: 配置{torpSlots}");
            if (p.TryGetValue("torpedoes_reload_time", out var torpReload)) sb.Append($" | 装填{torpReload}s");
        }

        // 副炮（atbas 分项下的 HE 弹参数）
        if (p.TryGetValue("atbas_HE_name", out var secName))
        {
            sb.Append($"\n  副炮: {secName}");
            if (p.TryGetValue("atbas_distance", out var secRange)) sb.Append($" | 射程{secRange}km");
            if (p.TryGetValue("atbas_damage", out var secDmg)) sb.Append($" | 伤害{secDmg}");
            if (p.TryGetValue("atbas_shot_delay", out var secReload)) sb.Append($" | 装填{secReload}s");
        }

        // 存活
        sb.Append("\n  存活:");
        if (p.TryGetValue("health_hull_health", out var hp)) sb.Append($" 血量{hp}");
        if (p.TryGetValue("health_armour_range", out var armor)) sb.Append($" | 装甲{armor}mm");
        if (p.TryGetValue("health_armour_flood_prob", out var flood)) sb.Append($" | 鱼雷防护{flood}%");

        // 机动
        sb.Append("\n  机动:");
        if (p.TryGetValue("mobility_max_speed", out var spd)) sb.Append($" 航速{spd}节");
        if (p.TryGetValue("mobility_rudder_time", out var rudder)) sb.Append($" | 舵效{rudder}s");
        if (p.TryGetValue("mobility_turning_radius", out var radius)) sb.Append($" | 转向半径{radius}m");

        // 隐蔽
        sb.Append("\n  隐蔽:");
        if (p.TryGetValue("concealment_detect_distance_by_ship", out var sea)) sb.Append($" 海面{sea}km");
        if (p.TryGetValue("concealment_detect_distance_by_plane", out var air)) sb.Append($" | 空中{air}km");
        if (p.TryGetValue("concealment_detect_distance_by_submarine", out var sub)) sb.Append($" | 对潜{sub}km");

        // 防空（简要）
        if (p.TryGetValue("anti_aircraft_summary", out var aa))
            sb.Append($"\n  防空: {aa}");

        // AI 评价（来源: 360 战舰助手服务端预生成, 可能为空/缺失）
        var aiReview = ship["ai_review"]?.ToString();
        if (!string.IsNullOrWhiteSpace(aiReview))
            sb.Append($"\n  AI评价(360): {aiReview}");

        return sb.ToString();
    }

    /// <summary>
    /// 递归收集一艘船的所有参数为扁平字典。
    /// key 用原始 json key（如 artillery_shot_delay），value 取简单值字符串。
    /// 嵌套结构（如 AP 弹详情）用 "前缀.子key" 的形式记录。
    /// </summary>
    private Dictionary<string, string> CollectParams(JsonObject ship)
    {
        var dict = new Dictionary<string, string>();
        var infoList = ship["ship_info_list"] as JsonArray;
        if (infoList == null) return dict;

        foreach (var sec in infoList)
        {
            if (sec is not JsonObject section) continue;
            var secKey = section["key"]?.ToString() ?? "";
            var deployList = section["deploy_list"] as JsonArray;
            if (deployList == null) continue;

            foreach (var dep in deployList)
            {
                if (dep is not JsonObject d) continue;
                var paramList = d["parameter_list"] as JsonArray;
                if (paramList == null) continue;
                foreach (var pp in paramList)
                {
                    if (pp is not JsonObject param) continue;
                    CollectParamRecursive(param, dict, secKey);
                }
            }
        }

        // 防空汇总
        BuildAaSummary(infoList, dict);
        return dict;
    }

    private void CollectParamRecursive(JsonObject param, Dictionary<string, string> dict, string prefix)
    {
        var key = param["key"]?.ToString();
        var name = param["name"]?.ToString();
        var value = param["value"];

        if (string.IsNullOrEmpty(key)) return;

        if (value is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (!string.IsNullOrWhiteSpace(s)) dict[key] = s.Trim();
        }
        else if (value is JsonValue vn && vn.TryGetValue<int>(out var i))
        {
            dict[key] = i.ToString(CultureInfo.InvariantCulture);
        }
        else if (value is JsonValue vd && vd.TryGetValue<double>(out var d))
        {
            dict[key] = d.ToString("0.###", CultureInfo.InvariantCulture);
        }
        else if (value is JsonArray arr)
        {
            // 嵌套数组（如 AP/HE 弹详情、防空炮列表）
            foreach (var child in arr)
            {
                if (child is JsonObject childObj)
                {
                    CollectParamRecursive(childObj, dict, prefix + "." + key);
                }
            }
            // 特殊处理：炮弹类记录其名称作为父级标识
            if (name != null && (key == "AP" || key == "HE"))
            {
                dict[prefix + "." + key + "_label"] = name;
            }
        }
    }

    /// <summary>把防空炮列表汇总成一句话，如 "基础配置4门(20mm厄利空/40mm博福斯)"</summary>
    private void BuildAaSummary(JsonArray infoList, Dictionary<string, string> dict)
    {
        var aaSection = infoList.FirstOrDefault(s =>
            s is JsonObject o && o["key"]?.ToString() == "anti_aircraft") as JsonObject;
        if (aaSection == null) return;

        var deploy = aaSection["deploy_list"] as JsonArray;
        if (deploy == null || deploy.Count == 0) return;

        var parts = new List<string>();
        foreach (var dep in deploy)
        {
            if (dep is not JsonObject d) continue;
            var depName = d["name"]?.ToString() ?? "";
            var paramList = d["parameter_list"] as JsonArray;
            if (paramList == null || paramList.Count == 0) continue;

            // parameter_list 每项是一门防空炮，其 value 数组含该炮参数
            var gunNames = new List<string>();
            foreach (var gun in paramList)
            {
                if (gun is not JsonObject g) continue;
                var valueArr = g["value"] as JsonArray;
                if (valueArr == null) continue;
                foreach (var v in valueArr)
                {
                    if (v is JsonObject vo && vo["key"]?.ToString() == "anti_aircraft_name")
                    {
                        var nm = vo["value"]?.ToString();
                        if (!string.IsNullOrEmpty(nm)) gunNames.Add(nm);
                        break;
                    }
                }
            }
            if (gunNames.Count > 0)
            {
                var distinct = gunNames.Distinct();
                parts.Add($"{depName}{gunNames.Count}门({string.Join("/", distinct)})");
            }
        }
        if (parts.Count > 0)
            dict["anti_aircraft_summary"] = string.Join(", ", parts);
    }

    private static readonly Dictionary<string, string> NationMap = new()
    {
        ["usa"] = "美系", ["germany"] = "德系", ["japan"] = "日系", ["ussr"] = "苏系",
        ["uk"] = "英系", ["france"] = "法系", ["italy"] = "意系", ["pan_asia"] = "泛亚",
        ["pan_america"] = "泛美", ["europe"] = "泛欧", ["netherlands"] = "荷兰",
        ["spain"] = "西班牙", ["commonwealth"] = "英联邦", ["pan_america"] = "泛美",
    };
}

internal static class DictExt
{
    public static string GetOrEmpty(this Dictionary<string, string> d, string key)
        => d.TryGetValue(key, out var v) ? v : "";
}
