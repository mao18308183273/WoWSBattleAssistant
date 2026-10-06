using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 实时预瞄（炮击提前量）监视器。
///
/// 【它是什么】
/// 启动 Tools\live_aim.py 的 --watch 模式，持续增量读游戏正在写的
/// replays\temp.wowsreplay，每 0.5 秒解算一次"打移动目标要往哪瞄"，
/// 结果写成 live_aim.json，本类定时读它并翻译成中文给 UI / AI 用。
///
/// 【数据可信度】
/// 全部来自回放里的服务端权威数据：目标逐 tick 精确坐标 + 航向 + 真实弹速，
/// 提前量用迭代解 |T0 + V·t − P| = s·t 算出（8 次迭代收敛到 1e-7）。
/// 距离/坐标已按标定系数换算成米（1 坐标单位 = 8.15 米，见 Tools/coord_scale.py）。
/// 没有任何视觉估算。
///
/// 【与离线版的关系】
/// 离线版 Tools\aim_lead.py 走 wows-replay-parser，数据完整可用；
/// 本实时版对 MinimapVision(type 0x2c) 包的"观察者→被观察者"列表结构
/// 尚未完全逆向，因此可见性判定可能偏保守 —— 宁可少报也不误报。
/// </summary>
public sealed class LiveAimMonitor : IDisposable
{
    public string SnapshotPath { get; }
    public string? ReplaysDir { get; set; }
    public bool Running => _proc is { HasExited: false };
    public string LastStatus { get; private set; } = "未启动";
    public DateTime LastUpdate { get; private set; } = DateTime.MinValue;
    public int TargetCount { get; private set; }

    /// <summary>当前弹速（m/s）。可由 UI 切换 AP/HE，因为两者弹速不同。</summary>
    public double ShellSpeed { get; set; } = 900.0;

    private Process? _proc;
    private Timer? _timer;
    private AimSnapshot? _snap;
    private bool _disposed;

    public LiveAimMonitor()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WoWSBattleAssistant");
        SnapshotPath = Path.Combine(dir, "live_aim.json");
    }

    public bool Start(string? pythonExe, string? replaysDir)
    {
        if (!string.IsNullOrWhiteSpace(replaysDir)) ReplaysDir = replaysDir;
        if (string.IsNullOrWhiteSpace(ReplaysDir))
        {
            LastStatus = "未指定回放目录";
            return false;
        }
        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "live_aim.py");
        if (!File.Exists(script)) { LastStatus = $"缺少脚本：{script}"; return false; }

        var py = pythonExe;
        if (string.IsNullOrWhiteSpace(py)) py = SpawnDbUpdater.FindPython();
        if (string.IsNullOrWhiteSpace(py)) { LastStatus = "未找到 Python 3"; return false; }

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
            if (_proc == null) { LastStatus = "无法启动预瞄进程"; return false; }
            LastStatus = "已启动，等待对局";
            AppLog.Info($"实时预瞄：已启动（{ReplaysDir}）");
            _timer = new Timer(_ => Read(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(700));
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = "启动失败：" + ex.Message;
            AppLog.Warn("实时预瞄启动失败：" + ex.Message);
            return false;
        }
    }

    public void Stop()
    {
        _timer?.Dispose(); _timer = null;
        try { if (_proc is { HasExited: false }) { _proc.Kill(true); _proc.WaitForExit(3000); } }
        catch { }
        _proc = null;
        LastStatus = "已停止";
    }

    private void Read()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return;
            var txt = File.ReadAllText(SnapshotPath);
            if (string.IsNullOrWhiteSpace(txt)) return;
            var s = JsonSerializer.Deserialize<AimSnapshot>(txt, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (s == null) return;
            _snap = s;
            LastUpdate = DateTime.Now;
            TargetCount = s.Targets?.Count ?? 0;
            LastStatus = s.Status == "waiting" ? "等待对局" : $"解算中（{TargetCount} 个目标）";
        }
        catch { /* 文件可能正在被写，下一拍再读 */ }
    }

    /// <summary>取当前快照（每次调用都先尝试刷新，保证给 AI 的是最新一帧）。</summary>
    public AimSnapshot? Current
    {
        get { try { Read(); } catch { } return _snap; }
    }

    /// <summary>
    /// 生成给 AI 的预瞄文本。刻意写清"这是计算结果、不是观测结果"，
    /// 并保留样本可信度（速度为 0 表示目标当前静止，提前量无意义）。
    /// </summary>
    public string BuildText(int maxTargets = 8)
    {
        var s = Current;
        if (s?.Targets == null || s.Targets.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("【炮击提前量解算（真实坐标 + 真实弹速的迭代解，非视觉估算）】");
        if (s.Me != null)
        {
            double px = s.Me.PosM != null && s.Me.PosM.Count > 0 ? s.Me.PosM[0] : 0;
            double pz = s.Me.PosM != null && s.Me.PosM.Count > 1 ? s.Me.PosM[1] : 0;
            sb.Append($"我方位置：({px}, {pz}) 米");
            if (s.Me.HeadingDeg is double h) sb.Append($"　舰艏向 {h:0}°");
            if (s.Me.SpeedKn is double sp) sb.Append($"　航速 {sp:0} 节");
            sb.AppendLine();
        }
        if (s.ShellSpeed > 0) sb.AppendLine($"按弹速 {s.ShellSpeed:0} m/s 计算：");
        sb.AppendLine();

        var vis = s.Targets.Where(t => t.Visible).ToList();
        if (vis.Count == 0)
        {
            sb.AppendLine("当前没有已点亮的敌方目标（数据只覆盖可见目标，不做推测）。");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("  目标            距离     速度    飞行时间  提前量   应瞄方位   命中估计");
        foreach (var t in vis.Take(maxTargets))
        {
            var move = t.SpeedKn >= 1.0;
            sb.AppendLine(string.Format("  {0,-14} {1,5} m {2,5} 节 {3,7} s {4,5} m  {5,6}°→{6,6}°   {7,4}%",
                Clip(t.Name, 14),
                t.Dist,
                t.SpeedKn,
                t.Flight,
                move ? t.Lead.ToString() : "—",
                t.BrgNow,
                t.BrgAim,
                (int)(t.HitProb * 100)));
        }
        sb.AppendLine();
        sb.AppendLine("  说明：提前量 = 目标在炮弹飞行时间内会移动的距离；"
                    + "把准星从「当前方位」转到「应瞄方位」就是完整的提前量打点。");
        if (vis.Any(t => t.HitProb < 0.35))
            sb.AppendLine("  ⚠ 部分目标命中估计偏低（距离远、散布大），建议先拉近距离或换 HE。");
        return sb.ToString().TrimEnd();
    }

    private static string Clip(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        return s.Length <= n ? s : s[..n];
    }

    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }

    // ---------------- 数据模型 ----------------

    public sealed class AimSnapshot
    {
        public string? Status { get; set; }
        public string? Updated { get; set; }
        public double Clock { get; set; }
        public double K_Meters { get; set; }
        public double ShellSpeed { get; set; }
        public MeInfo? Me { get; set; }
        public List<Target>? Targets { get; set; }
    }

    public sealed class MeInfo
    {
        public List<double>? PosM { get; set; }
        public double? HeadingDeg { get; set; }
        public double? SpeedKn { get; set; }
    }

    public sealed class Target
    {
        public long Eid { get; set; }
        public string? Name { get; set; }
        public bool Visible { get; set; }
        public double Dist { get; set; }
        public double SpeedKn { get; set; }
        public bool Moving { get; set; }
        public double Flight { get; set; }
        public double Lead { get; set; }
        public double BrgNow { get; set; }
        public double BrgAim { get; set; }
        public double Swing { get; set; }
        public double Sigma { get; set; }
        public double HitProb { get; set; }
    }
}
