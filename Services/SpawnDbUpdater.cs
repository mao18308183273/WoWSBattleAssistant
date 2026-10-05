using System.Diagnostics;
using System.IO;
using System.Text;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 「打一局记一笔」的自动闭环：把游戏 replays 目录里新出现的 .wowsreplay 喂给
/// Tools/replay_spawn_scan.py，累加进用户侧的出生点知识库。玩得越多越精确。
///
/// 【为什么必须走回放而不是运行时】
/// 实机验证过：ModsAPI 在运行中被官方裁剪，拿不到任何坐标（battle 模块只有 5 个方法，
/// Player 9 个字段里没有 position/isVisible；Lesta/BigWorld/camera 全部不可达）。
/// 客户端资源里也搜不到剧本出生点定义——出生点由服务端下发，但会被完整写进 .wowsreplay。
/// 所以唯一合法且完整的来源就是回放。
///
/// 【为什么一定要落用户目录】
/// 程序目录（Program Files）往往没写权限，且升级会被覆盖。所以永远写
/// %AppData%\WoWSBattleAssistant\spawn_db.json，ScenarioSpawnKnowledge 也优先读它。
/// </summary>
public static class SpawnDbUpdater
{
    /// <summary>扫描脚本随程序发布的位置（Tools\replay_spawn_scan.py）。</summary>
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "Tools", "replay_spawn_scan.py");

    /// <summary>上次更新结果（供 UI 显示）。</summary>
    public static string LastResult { get; private set; } = "尚未更新";

    private static bool _pythonMissingWarned;

    // ------------------------------------------------------------------ Python 探测

    /// <summary>
    /// 找一个能跑的 Python 3。扫描脚本是纯标准库实现（Blowfish 都是手写的），
    /// 所以任何 Python 3.7+ 都行，不需要装任何第三方包。
    /// </summary>
    public static string? FindPython(string? configured = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            candidates.Add(configured!);

        // PATH 上的
        candidates.Add("python");
        candidates.Add("py");
        candidates.Add("python3");

        // 常见安装位置兜底（PATH 没配好的情况）
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(local, "Programs", "Python", "Python312", "python.exe"));
        candidates.Add(Path.Combine(local, "Programs", "Python", "Python313", "python.exe"));
        candidates.Add(Path.Combine(local, "Programs", "Python", "Python311", "python.exe"));
        candidates.Add(Path.Combine(pf, "Python312", "python.exe"));
        candidates.Add(Path.Combine(pf, "Python313", "python.exe"));

        foreach (var c in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = c,
                    Arguments = "-c \"import sys;sys.stdout.write(str(sys.version_info[0]))\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var so = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (p.ExitCode == 0 && so.StartsWith("3")) return c;
            }
            catch { /* 换下一个候选 */ }
        }
        return null;
    }

    // ------------------------------------------------------------------ 更新

    public sealed class UpdateOutcome
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public int Added { get; set; }
    }

    /// <summary>
    /// 扫描游戏回放目录，把新回放累加进知识库。全程后台、失败不影响主程序。
    /// </summary>
    /// <param name="gamePath">游戏安装目录（其下有 replays\）。</param>
    /// <param name="pythonExe">可选的 Python 路径（留空自动探测）。</param>
    /// <param name="rebuild">true = 忽略"已收录"清单，用全部回放重算（格式升级时用）。</param>
    public static UpdateOutcome Update(string? gamePath, string? pythonExe = null, bool rebuild = false)
    {
        var script = ScriptPath;
        if (!File.Exists(script))
            return Fail($"找不到扫描脚本：{script}");

        string? replays = null;
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            var cand = Path.Combine(gamePath!, "replays");
            if (Directory.Exists(cand)) replays = cand;
        }
        if (replays == null)
            return Fail("未找到游戏回放目录（请在设置里填好游戏安装目录）");

        var py = FindPython(pythonExe);
        if (py == null)
        {
            if (!_pythonMissingWarned)
            {
                _pythonMissingWarned = true;
                AppLog.Warn("出生点知识库：未找到 Python 3，跳过回放扫描。" +
                            "可在设置里手动指定 python.exe 路径。");
            }
            return Fail("未找到 Python 3（可在设置里手动指定 python.exe）");
        }

        var dbPath = ScenarioSpawnKnowledge.UserDbPath;
        try
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            // 第一次运行时把随程序发布的基线库复制过去，作为起始样本
            if (!File.Exists(dbPath))
            {
                var baseline = Path.Combine(AppContext.BaseDirectory, "Tools", "spawn_db.json");
                if (File.Exists(baseline)) File.Copy(baseline, dbPath, true);
            }
        }
        catch (Exception ex)
        {
            return Fail($"准备知识库目录失败：{ex.Message}");
        }

        var args = new StringBuilder();
        args.Append('"').Append(script).Append("\" --db \"").Append(dbPath).Append('"');
        if (rebuild) args.Append(" --rebuild");
        args.Append(" \"").Append(replays).Append('"');

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = py,
                Arguments = args.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return Fail("无法启动 Python 进程");

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                try { p.Kill(true); } catch { }
                return Fail("回放扫描超时（>120 秒）");
            }
            var so = stdout.GetAwaiter().GetResult() ?? "";
            var se = stderr.GetAwaiter().GetResult() ?? "";

            if (p.ExitCode != 0)
                return Fail($"扫描脚本返回 {p.ExitCode}：{(se.Length > 0 ? se : so)}");

            // 解析 "新增 N 局，跳过 M，失败 K"
            var added = 0;
            foreach (var line in so.Split('\n'))
            {
                if (!line.Contains("新增")) continue;
                var t = line.Replace("新增", "|").Replace("局", "|").Split('|');
                if (t.Length >= 2 && int.TryParse(t[1].Trim(), out var n)) added = n;
            }

            // 让知识库下次读取时重新加载
            ScenarioSpawnKnowledge.Invalidate();

            var msg = added > 0 ? $"新增 {added} 局回放，出生点知识库已更新" : "没有新回放，知识库已是最新";
            LastResult = msg;
            AppLog.Info($"出生点知识库：{msg}");
            if (added > 0)
            {
                foreach (var line in so.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("[OK]")) AppLog.Info("  回放 " + line.Trim());
                    else if (line.TrimStart().StartsWith("[跳过]")) AppLog.Warn("  回放 " + line.Trim());
                }
            }
            return new UpdateOutcome { Ok = true, Message = msg, Added = added };
        }
        catch (Exception ex)
        {
            return Fail($"回放扫描异常：{ex.Message}");
        }
    }

    private static UpdateOutcome Fail(string msg)
    {
        LastResult = msg;
        return new UpdateOutcome { Ok = false, Message = msg };
    }

    /// <summary>供 UI 显示的一行状态。</summary>
    public static string StatusLine()
    {
        var know = ScenarioSpawnKnowledge.StatusText();
        var py = FindPython();
        var pyNote = py == null ? "（未找到 Python，自动更新已停用）" : "";
        return $"{know}　最近：{LastResult}{pyNote}";
    }
}
