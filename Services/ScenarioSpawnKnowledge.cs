using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 行动/剧情（PVE）「AI 出生点」知识库。
///
/// 【为什么必须走回放】
/// ModsAPI 在运行中被官方刻意裁剪，拿不到任何坐标（实机验证：battle 模块只有 5 个方法，
/// Player 对象 9 个字段里没有 position/isVisible；Lesta/BigWorld/camera 全部不可达）。
/// 客户端资源里也搜不到剧本出生点定义（basecontent.idx 0 命中，地图 pkg 只有
/// minimap.png / space.bin / locations.xml，无 scenario/spawn 数据）——出生点由服务端下发。
/// 但服务端下发的出生点**每局固定**，且会被完整写进 .wowsreplay，所以可以「打一局记一笔」：
///   Tools/replay_spawn_scan.py 解密(Blowfish)→解压(zlib)→解析包流 → 抽取每艘舰的
///   首次出现坐标与时刻 → 按剧本聚合成 Tools/spawn_db.json。玩得越多越精确。
///
/// 【本类职责】把 spawn_db.json 翻成一段可以直接喂给 AI 的中文简报：
///   波次时间轴 / 下一波预测 / 每个角色的出生点与相对方位。
/// </summary>
public static class ScenarioSpawnKnowledge
{
    private static SpawnDatabase? _db;
    private static bool _loaded;

    /// <summary>逐舰出生点最多喂给 AI 多少条（防止打得多以后 prompt 爆炸）。</summary>
    private const int MaxShipLines = 45;

    // ------------------------------------------------------------------ 数据模型

    public sealed class SpawnDatabase
    {
        public string? Generated { get; set; }
        public Dictionary<string, ScenarioEntry> Scenarios { get; set; } = new();
    }

    public sealed class ScenarioEntry
    {
        public string? Scenario { get; set; }
        public string? Map { get; set; }
        public int? MapId { get; set; }
        public int? ScenarioConfigId { get; set; }
        public List<string> Runs { get; set; } = new();

        [JsonPropertyName("spawnList")]
        public List<SpawnPoint> SpawnList { get; set; } = new();

        public List<WaveInfo> Waves { get; set; } = new();

        /// <summary>
        /// 我方（真人）出生点基准。真人昵称每局都不同，不能当聚合键，
        /// 所以单独压成一个带分布范围的基准点，不会把知识库塞满一次性昵称。
        /// </summary>
        public OwnSpawnPoint? OwnSpawn { get; set; }
    }

    public sealed class OwnSpawnPoint
    {
        public int Samples { get; set; }
        public int Runs { get; set; }
        public double X { get; set; }
        public double Z { get; set; }
        public double[]? XRange { get; set; }
        public double[]? ZRange { get; set; }
    }

    public sealed class SpawnPoint
    {
        /// <summary>剧本内的稳定角色名（如 IDS_OP_01_04_ATAKER_C1），每局不变，是跨局聚合的键。</summary>
        public string? Role { get; set; }
        public int? Team { get; set; }
        public bool? IsBot { get; set; }
        public int? MaxHealth { get; set; }
        public int Samples { get; set; }

        /// <summary>出生时刻（开局后秒数，多局平均）。</summary>
        public double T { get; set; }
        public double[]? TRange { get; set; }
        public double X { get; set; }
        public double Z { get; set; }
        public double[]? XRange { get; set; }
        public double[]? ZRange { get; set; }
    }

    public sealed class WaveInfo
    {
        public int Index { get; set; }
        public double T { get; set; }
        public int Count { get; set; }
        public List<string> Ships { get; set; } = new();
    }

    // ------------------------------------------------------------------ 加载

    /// <summary>让下一次读取重新加载（更新脚本写完之后调用）。</summary>
    public static void Invalidate()
    {
        _db = null;
        _loaded = false;
        _resolvedPath = null;
    }

    /// <summary>用户侧持续累加的库（程序会自动把新回放喂进去）。优先级最高。</summary>
    public static string UserDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WoWSBattleAssistant", "spawn_db.json");

    private static IEnumerable<string> CandidatePaths()
    {
        // 1) 用户侧累计库——玩得越多越精确，必须优先于随程序发布的基线库
        yield return UserDbPath;
        // 2) 程序目录下 Tools\spawn_db.json（发布时随程序一起拷贝的基线）
        yield return Path.Combine(AppContext.BaseDirectory, "Tools", "spawn_db.json");
        // 3) 开发期：工程根目录
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Tools", "spawn_db.json");
    }

    public static bool TryLoad(out string? path)
    {
        path = null;
        if (_loaded && _db != null) { path = _resolvedPath; return true; }

        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        foreach (var p in CandidatePaths())
        {
            try
            {
                if (!File.Exists(p)) continue;
                var txt = File.ReadAllText(p);
                var db = JsonSerializer.Deserialize<SpawnDatabase>(txt, opts);
                if (db?.Scenarios == null || db.Scenarios.Count == 0) continue;
                _db = db; _loaded = true; _resolvedPath = p; path = p;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"出生点知识库解析失败({p}): {ex.Message}");
            }
        }
        _loaded = true;
        return false;
    }

    private static string? _resolvedPath;

    /// <summary>按剧本代号精确匹配；匹配不到再按地图名兜底。</summary>
    public static ScenarioEntry? Find(string? scenarioKey, string? mapName = null)
    {
        if (!TryLoad(out _)) return null;
        if (_db == null) return null;

        if (!string.IsNullOrWhiteSpace(scenarioKey) &&
            _db.Scenarios.TryGetValue(scenarioKey.Trim(), out var hit))
            return hit;

        if (!string.IsNullOrWhiteSpace(mapName))
        {
            var m = mapName.Trim();
            return _db.Scenarios.Values.FirstOrDefault(
                s => string.Equals(s.Map, m, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    // ------------------------------------------------------------------ 生成简报

    /// <summary>
    /// 生成给 AI 的行动模式出生点简报。
    /// </summary>
    /// <param name="scenarioKey">剧本代号（tempArenaInfo 的 scenario 字段）</param>
    /// <param name="mapName">地图显示名，兜底匹配用</param>
    /// <param name="elapsedSeconds">开局至今秒数（用于预测下一波）</param>
    /// <param name="seenRoles">本局已经出现过的剧本角色名（mod 实时名单），用来判断"已出/未出"</param>
    public static string BuildBriefing(string? scenarioKey, string? mapName,
                                       double elapsedSeconds, IEnumerable<string>? seenRoles = null)
    {
        var e = Find(scenarioKey, mapName);
        if (e == null) return string.Empty;

        var seen = seenRoles == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(seenRoles.Where(s => !string.IsNullOrEmpty(s))!,
                                  StringComparer.OrdinalIgnoreCase);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【行动模式 · AI 出生点知识库（回放实测累计，权威值）】");
        sb.AppendLine($"剧本：{e.Scenario}　地图：{e.Map}　样本局数：{e.Runs.Count}");
        sb.AppendLine("坐标系：地图中心 (0,0)，x 向东为正、z 向北为正（与游戏小地图一致，上=北）。单位为米。");
        sb.AppendLine("说明：出生点由服务端下发、每局固定；下列坐标是 " + e.Runs.Count +
                      " 局回放的平均值，样本越多越准。");
        sb.AppendLine();

        // --- 我方出生点作为方位参照（ownSpawn = 多局真人出生位置的均值） ---
        double ox = 0, oz = 0;
        bool hasOrigin = false;
        if (e.OwnSpawn != null && e.OwnSpawn.Samples > 0)
        {
            ox = e.OwnSpawn.X; oz = e.OwnSpawn.Z; hasOrigin = true;
            var spread = (e.OwnSpawn.XRange?.Length == 2 && e.OwnSpawn.ZRange?.Length == 2)
                ? $"，分布范围 x[{e.OwnSpawn.XRange[0]:0},{e.OwnSpawn.XRange[1]:0}] " +
                  $"z[{e.OwnSpawn.ZRange[0]:0},{e.OwnSpawn.ZRange[1]:0}]"
                : "";
            sb.AppendLine($"我方出生基准点：({ox:0}, {oz:0})（{e.OwnSpawn.Runs} 局共 " +
                          $"{e.OwnSpawn.Samples} 艘玩家舰的 t=0 位置均值{spread}）");
            sb.AppendLine();
        }

        // --- 波次时间轴 ---
        var waves = e.Waves ?? new List<WaveInfo>();
        if (waves.Count > 0)
        {
            sb.AppendLine("波次时间轴（t = 开局后经过的秒数）：");
            foreach (var w in waves)
            {
                var spawn = e.SpawnList.Where(s => w.Ships.Contains(s.Role ?? "")).ToList();

                // 关键：同一波里可能同时有「我方要护送的目标」和「敌方进攻舰」，
                // 混在一起求平均会得出一个两边都不在的假集结点，所以按阵营分开给。
                var parts = new List<string>();
                foreach (var g in spawn.Where(s => s.Team == 0 || s.Team == 1)
                                       .GroupBy(s => s.Team!.Value).OrderBy(g => g.Key))
                {
                    var cx = g.Average(s => s.X);
                    var cz = g.Average(s => s.Z);
                    var tag = g.Key == 0 ? "我方" : "敌方";
                    var loc = $"{tag}({cx:0}, {cz:0})";
                    if (hasOrigin)
                    {
                        var d = Math.Sqrt((cx - ox) * (cx - ox) + (cz - oz) * (cz - oz));
                        if (d >= 30) loc += $" 在我方{DescribeDirection(cx - ox, cz - oz)} {d:0} 米";
                    }
                    parts.Add(loc);
                }

                var roles = spawn.Where(s => s.IsBot == true)
                                 .Select(s => ModPlayer.DescribeScenarioRole(s.Role ?? ""))
                                 .Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
                sb.AppendLine($"  第{w.Index}波 t≈{w.T:0}s　{w.Count} 艘" +
                              (parts.Count > 0 ? "　" + string.Join("，", parts) : "") +
                              (roles.Count > 0 ? $"　类型：{string.Join("、", roles)}" : ""));
            }
            sb.AppendLine();

            // --- 下一波预测 ---
            var passed = waves.Where(w => w.T <= elapsedSeconds).ToList();
            var next = waves.FirstOrDefault(w => w.T > elapsedSeconds);
            sb.Append($"当前 t≈{elapsedSeconds:0}s：已出现 {passed.Count} 波（共 " +
                      $"{passed.Sum(w => w.Count)} 艘）");
            if (next != null)
            {
                var gap = next.T - elapsedSeconds;
                var nspawn = e.SpawnList.Where(s => next.Ships.Contains(s.Role ?? "")).ToList();
                // 优先给「敌方」那部分的集结点——AI 关心的是敌人从哪来
                var foe = nspawn.Where(s => s.Team == 1).ToList();
                var pick = foe.Count > 0 ? foe : nspawn;
                var nx = pick.Count > 0 ? pick.Average(s => s.X) : 0;
                var nz = pick.Count > 0 ? pick.Average(s => s.Z) : 0;
                var ndir = hasOrigin ? DescribeDirection(nx - ox, nz - oz) : "";
                var ndist = hasOrigin ? Math.Sqrt((nx - ox) * (nx - ox) + (nz - oz) * (nz - oz)) : 0;
                sb.AppendLine($"；下一波（第{next.Index}波）预计 t≈{next.T:0}s（约 {gap:0} 秒后）" +
                              (foe.Count > 0 ? "敌人" : "舰船") +
                              $"从 ({nx:0}, {nz:0}) 投入 {next.Count} 艘" +
                              (hasOrigin && ndist >= 30 ? $"，在我方{ndir} {ndist:0} 米" : "") + "。");
            }
            else
            {
                sb.AppendLine("；所有已知波次均已出现。");
            }
            sb.AppendLine();
        }

        // --- 逐舰出生点 ---
        // 人机对战里 AI 名字是从池子里随机抽的（:Spruance: 这类），打得多会冒出上百个键。
        // 这里做预算控制：优先保留「本局还没出现」的（这才是预测价值所在），其余按时间截。
        var list = e.SpawnList.Where(s => !string.IsNullOrEmpty(s.Role)).ToList();
        if (list.Count > MaxShipLines)
        {
            list = list.Where(s => seen.Count == 0 || !seen.Contains(s.Role!))
                       .Concat(list.Where(s => seen.Count > 0 && seen.Contains(s.Role!)))
                       .Take(MaxShipLines).ToList();
            sb.AppendLine($"逐舰出生点（共 {e.SpawnList.Count} 条，按时间截取前 {MaxShipLines} 条，未出现的优先）：");
        }
        else
        {
            sb.AppendLine("逐舰出生点：");
        }

        foreach (var s in list)
        {
            if (string.IsNullOrEmpty(s.Role)) continue;
            var role = ModPlayer.DescribeScenarioRole(s.Role) ?? (s.IsBot == true ? "敌方AI" : "我方舰");
            var side = s.Team == 0 ? "（我方阵营）" : s.Team == 1 ? "（敌方阵营）" : "";
            var appear = seen.Count > 0
                ? (seen.Contains(s.Role) ? "本局已出现" : "本局尚未出现")
                : "";
            var rel = hasOrigin
                ? $"，我方{DescribeDirection(s.X - ox, s.Z - oz)} " +
                  $"{Math.Sqrt((s.X - ox) * (s.X - ox) + (s.Z - oz) * (s.Z - oz)):0} 米"
                : "";
            var spread = "";
            if (s.Samples > 1 && s.XRange?.Length == 2 && s.ZRange?.Length == 2)
                spread = $"，多局波动 x[{s.XRange[0]:0},{s.XRange[1]:0}] z[{s.ZRange[0]:0},{s.ZRange[1]:0}]";
            sb.AppendLine($"  {ShortRole(s.Role)}｜{role}{side}｜t≈{s.T:0}s 出生点({s.X:0}, {s.Z:0})" +
                          $"{rel}｜样本{s.Samples}{spread}" +
                          (string.IsNullOrEmpty(appear) ? "" : $"｜{appear}"));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>把 IDS_OP_01_04_ATAKER_C1 简写成 OP_01_04·ATAKER_C1，省 token。</summary>
    private static string ShortRole(string role)
    {
        var r = role.StartsWith("IDS_", StringComparison.Ordinal) ? role.Substring(4) : role;
        return r.Replace('_', '·');
    }

    /// <summary>按相对偏移给出中文方位（dz 记为北）。中文习惯东西在前：东北/东南/西北/西南。</summary>
    private static string DescribeDirection(double dx, double dz)
    {
        if (Math.Abs(dx) < 30 && Math.Abs(dz) < 30) return "附近";
        var ns = dz > 30 ? "北" : dz < -30 ? "南" : "";
        var ew = dx > 30 ? "东" : dx < -30 ? "西" : "";
        return ew + ns;   // 恒为「东西+南北」，避免出现"北西/南东"这种反习惯写法
    }

    /// <summary>供 UI 显示：知识库概况。</summary>
    public static string StatusText()
    {
        if (!TryLoad(out var path) || _db == null)
            return "出生点知识库：未找到（运行 Tools\\replay_spawn_scan.py 生成）";
        var pve = _db.Scenarios.Values.Count(s => s.Scenario?.StartsWith("PCVO") == true);
        return $"出生点知识库：{_db.Scenarios.Count} 个剧本（行动 {pve}），" +
               $"更新于 {_db.Generated}　[{Path.GetFileName(path)}]";
    }
}
