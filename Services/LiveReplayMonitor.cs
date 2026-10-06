using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 实时回放流监视器 —— 本项目目前信息量最大的一条数据通路。
///
/// 【为什么需要它】
/// ModsAPI 被官方裁剪过：battle 模块只剩 5 个方法，Player 对象 9 个字段里没有
/// position / isVisible，Lesta/BigWorld/camera 全部不可达 → 运行时拿不到任何坐标。
/// 但游戏会把服务端下发的**完整战场记录**持续写进 replays\temp.wowsreplay，
/// 里面逐 tick 记录了所有舰船的位置与航向。只读这个文件，不需要 mod、不碰内存、
/// 也**不受游戏版本更新影响**（mod 每次版本更新都要重装，这条路不会）。
///
/// 【做法】
/// 启动 Tools\replay_live.py 的 --watch 模式（纯标准库，零依赖），它增量解析
/// temp.wowsreplay 并持续把战场快照写成 JSON；本类定时读这个 JSON，
/// 翻译成一段可以直接喂给 AI 的中文文本。
/// </summary>
public sealed class LiveReplayMonitor : IDisposable
{
    /// <summary>快照 JSON 的落地位置（放在程序目录下，避免权限问题）。</summary>
    public string SnapshotPath { get; }

    public string? ReplaysDir { get; set; }
    public bool Running => _proc is { HasExited: false };
    public string LastStatus { get; private set; } = "未启动";
    public DateTime LastUpdate { get; private set; } = DateTime.MinValue;

    private Process? _proc;
    private Timer? _timer;
    private BattleSnapshot? _snapshot;
    private bool _disposed;

    public LiveReplayMonitor()
    {
        SnapshotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WoWSBattleAssistant", "live_battle.json");
    }

    // ---------------------------------------------------------------- 启动/停止

    /// <summary>
    /// 启动后台解析进程。失败不会抛异常（没有 Python 也能继续用其它功能）。
    /// </summary>
    public bool Start(string? pythonExe = null, string? replaysDir = null)
    {
        if (replaysDir != null) ReplaysDir = replaysDir;
        if (string.IsNullOrWhiteSpace(ReplaysDir))
        {
            LastStatus = "未指定回放目录";
            return false;
        }

        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "replay_live.py");
        if (!File.Exists(script))
        {
            LastStatus = $"缺少脚本：{script}";
            AppLog.Warn(LastStatus);
            return false;
        }

        var py = pythonExe;
        if (string.IsNullOrWhiteSpace(py)) py = SpawnDbUpdater.FindPython();
        if (string.IsNullOrWhiteSpace(py))
        {
            LastStatus = "未找到 Python 3，实时回放解析未启用";
            AppLog.Warn(LastStatus);
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(SnapshotPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            var psi = new ProcessStartInfo
            {
                FileName = py!,
                Arguments = $"\"{script}\" --watch \"{ReplaysDir}\" --out \"{SnapshotPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            _proc = Process.Start(psi);
            if (_proc == null)
            {
                LastStatus = "无法启动解析进程";
                return false;
            }
            LastStatus = "已启动，等待对局开始";
            AppLog.Info($"实时回放监视：已启动（目录 {ReplaysDir}）");

            _timer = new Timer(_ => ReadSnapshot(), null,
                               TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = "启动失败：" + ex.Message;
            AppLog.Warn("实时回放监视启动失败：" + ex.Message);
            return false;
        }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(true);
                _proc.WaitForExit(3000);
            }
        }
        catch { /* 进程可能已退出 */ }
        _proc = null;
        LastStatus = "已停止";
    }

    // ---------------------------------------------------------------- 读取快照

    private void ReadSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return;
            var txt = File.ReadAllText(SnapshotPath);
            if (string.IsNullOrWhiteSpace(txt)) return;
            var snap = JsonSerializer.Deserialize<BattleSnapshot>(txt, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (snap == null) return;
            _snapshot = snap;
            LastUpdate = DateTime.Now;
            LastStatus = snap.Status == "ended" ? "对局已结束" : "解析中";
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取实时快照失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 给 AI 的文本

    /// <summary>
    /// 把战场快照翻译成 AI 可直接使用的中文文本。
    /// 坐标系统与游戏小地图一致：中心 (0,0)，x 向东为正、z 向北为正，单位米。
    /// </summary>
    public string BuildBattleText()
    {
        // 先刷新一次：保证给 AI 的是最新的一帧，而不是定时器上次读到的旧值
        try { ReadSnapshot(); } catch { /* 读不到就用内存里上一帧 */ }
        var s = _snapshot;
        if (s == null || s.Ships == null || s.Ships.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("【实时战场态势（来自游戏回放流，逐 tick 精确坐标，权威值）】");
        sb.Append($"地图：{s.Map}");
        if (s.Duration > 0) sb.Append($"　已进行 {s.Clock:0}/{s.Duration} 秒");
        if (!string.IsNullOrEmpty(s.Scenario)) sb.Append($"　剧本：{s.Scenario}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(s.MyVehicle))
            sb.AppendLine($"我方所驾：{s.MyVehicle}{(string.IsNullOrEmpty(s.MyName) ? "" : "（" + s.MyName + "）")}");

        // 以自己为原点给方位，AI 更好用
        var me = s.Ships.FirstOrDefault(x => x.Relation == 0);
        double ox = me?.X ?? 0, oz = me?.Z ?? 0;

        var allies = s.Ships.Where(x => x.Relation is 0 or 1).ToList();
        var foes = s.Ships.Where(x => x.Relation == 2).ToList();

        sb.AppendLine();
        sb.AppendLine($"我方 {allies.Count} 艘（含自己）：");
        foreach (var a in allies)
            sb.AppendLine("  " + Describe(a, ox, oz, true));

        sb.AppendLine();
        sb.AppendLine($"敌方 {foes.Count} 艘：");
        // 按离自己的距离由近到远 —— 威胁最大的排前面
        foreach (var f in foes.OrderBy(f => Dist(f.X, f.Z, ox, oz)))
            sb.AppendLine("  " + Describe(f, ox, oz, false));

        if (s.Lineup?.Enemy?.Count > 0)
        {
            sb.AppendLine();
            sb.Append("敌方阵容（本局参战舰船，可用于判断对面配置）：");
            sb.Append(string.Join("、", s.Lineup.Enemy
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => e.Name)!.Take(14)));
        }
        return sb.ToString().TrimEnd();
    }

    private static string Describe(BattleShip s, double ox, double oz, bool isAlly)
    {
        var sb = new StringBuilder();
        sb.Append(isAlly ? (s.Relation == 0 ? "自己" : "队友") : "敌舰");
        if (!string.IsNullOrEmpty(s.Name)) sb.Append(' ').Append(s.Name);
        sb.Append($"　坐标({s.X:0}, {s.Z:0})");

        // 航向：yaw 是弧度，转成角度。0 度对应方向随版本可能有偏移，故标注为参考值。
        if (Math.Abs(s.Yaw) > 0.0001)
            sb.Append($"　航向约 {(s.Yaw * 180.0 / Math.PI):0}°");

        var d = Dist(s.X, s.Z, ox, oz);
        if (d >= 1)
            sb.Append($"　距我 {d:0} 米").Append('（').Append(Direction(s.X - ox, s.Z - oz)).Append('）');
        return sb.ToString();
    }

    private static double Dist(double x, double z, double ox, double oz)
        => Math.Sqrt((x - ox) * (x - ox) + (z - oz) * (z - oz));

    /// <summary>中文方位（东西在前；dz 记为北）。</summary>
    private static string Direction(double dx, double dz)
    {
        if (Math.Abs(dx) < 30 && Math.Abs(dz) < 30) return "附近";
        var ns = dz > 30 ? "北" : dz < -30 ? "南" : "";
        var ew = dx > 30 ? "东" : dx < -30 ? "西" : "";
        return ew + ns;
    }

    // ---------------------------------------------------------------- 数据模型

    public sealed class BattleSnapshot
    {
        public string? Status { get; set; }
        public string? Map { get; set; }
        public string? Scenario { get; set; }
        public double Clock { get; set; }
        public int? Duration { get; set; }
        public string? MyName { get; set; }
        public string? MyVehicle { get; set; }
        public int ShipCount { get; set; }
        public int Identified { get; set; }
        public List<BattleShip>? Ships { get; set; }
        public LineupInfo? Lineup { get; set; }
    }

    public sealed class BattleShip
    {
        public long Eid { get; set; }
        public double X { get; set; }
        public double Z { get; set; }
        public double Yaw { get; set; }
        public int? Relation { get; set; }
        public string? Name { get; set; }
        public long? ShipGlobalId { get; set; }
    }

    public sealed class LineupInfo
    {
        public List<LineupEntry>? Ally { get; set; }
        public List<LineupEntry>? Enemy { get; set; }
    }

    public sealed class LineupEntry
    {
        public string? Name { get; set; }
        public long? ShipGlobalId { get; set; }
        public int? Relation { get; set; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
