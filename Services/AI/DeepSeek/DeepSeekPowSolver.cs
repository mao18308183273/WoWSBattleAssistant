using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WoWSBattleAssistant.Services.AI.DeepSeek;

/// <summary>
/// DeepSeek PoW (DeepSeekHashV1) 求解器。
/// 通过启动 Node.js 子进程加载官方 sha3_wasm_bg.wasm 计算 answer。
/// pow_solver.js 与 wasm 作为嵌入资源,首次使用时释放到本地缓存目录。
/// </summary>
public static class DeepSeekPowSolver
{
    private const string JsResource = "WoWSBattleAssistant.Services.AI.DeepSeek.pow_solver.js";
    private const string WasmResource = "WoWSBattleAssistant.Services.AI.DeepSeek.sha3_wasm_bg.wasm";

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WoWSBattleAssistant", "pow");

    private static readonly string JsPath = Path.Combine(CacheDir, "pow_solver.js");
    private static readonly string WasmPath = Path.Combine(CacheDir, "sha3_wasm_bg.wasm");

    private static int _extracted; // 0=未释放, 1=已释放

    /// <summary>检查 Node.js 是否可用（不抛异常）</summary>
    public static bool IsAvailable()
    {
        try { return ResolveNode() != null; }
        catch { return false; }
    }
    private static string? _nodeExe;

    /// <summary>
    /// 求解 PoW,返回 answer。失败抛异常。
    /// </summary>
    /// <param name="challenge">挑战串(hex)</param>
    /// <param name="salt">盐</param>
    /// <param name="difficulty">难度(整数,如 144000)</param>
    /// <param name="expireAt">过期时间(毫秒时间戳,用于构造 prefix)</param>
    public static async Task<double> SolveAsync(string challenge, string salt, long difficulty, long expireAt,
        CancellationToken ct = default)
    {
        EnsureExtracted();
        var nodeExe = ResolveNode();
        if (nodeExe == null)
        {
            throw new InvalidOperationException(
                "未找到 Node.js。DeepSeek 分析需要 Node.js(v18+) 求解 PoW 验证。\n" +
                "请确认：\n" +
                "  1) 已安装 Node.js 18+ LTS（https://nodejs.org 下载，安装时勾选「Add to PATH」）\n" +
                "  2) 安装完成后重新启动本程序，让进程加载最新的 PATH\n" +
                "  3) 若不想装 Node.js，请设置 → 切换到【智谱 GLM】或【通义千问】（国内直连）\n" +
                "诊断：打开 cmd 输入 node --version 看是否有版本输出。");
        }

        var inputObj = new { challenge, salt, difficulty, expireAt };
        var inputJson = JsonSerializer.Serialize(inputObj);

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = $"\"{JsPath}\" \"{WasmPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!proc.Start())
            throw new InvalidOperationException("无法启动 Node.js 进程来求解 PoW。");

        // 写入 stdin
        await proc.StandardInput.WriteAsync(inputJson.AsMemory(), ct);
        await proc.StandardInput.FlushAsync(ct);
        proc.StandardInput.Close();

        // 总超时 30s(与 PoW expire_after 300s 相比足够宽松,实际求解多在百毫秒级)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        // 读取 stdout(只取第一行 JSON);stderr 仅用于诊断
        var stdoutTask = proc.StandardOutput.ReadLineAsync(cts.Token).AsTask();
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            string? line;
            try
            {
                line = await stdoutTask;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("PoW 求解超时(30s),可能是难度过高或 Node 环境异常。");
            }

            if (!proc.HasExited) proc.WaitForExit(2000);

            var stderr = await stderrTask;
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidOperationException($"PoW 求解器无输出。stderr: {Truncate(stderr, 500)}");

            var node = JsonNode.Parse(line);
            if (node?["error"] != null)
                throw new InvalidOperationException($"PoW 求解失败: {node["error"]}. stderr: {Truncate(stderr, 300)}");

            var answer = node?["answer"]?.GetValue<double>();
            if (answer == null)
                throw new InvalidOperationException($"PoW 求解器返回缺少 answer。原文: {Truncate(line, 300)}");

            return answer.Value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    /// <summary>释放嵌入资源到缓存目录(线程安全,只执行一次)。</summary>
    private static void EnsureExtracted()
    {
        if (Interlocked.CompareExchange(ref _extracted, 1, 0) == 1) return;

        try
        {
            Directory.CreateDirectory(CacheDir);
            var asm = Assembly.GetExecutingAssembly();
            WriteResource(asm, JsResource, JsPath);
            WriteResource(asm, WasmResource, WasmPath);
        }
        catch
        {
            _extracted = 0; // 允许下次重试
            throw;
        }
    }

    private static void WriteResource(Assembly asm, string name, string path)
    {
        using var rs = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"嵌入资源缺失: {name}");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        rs.CopyTo(fs);
    }

    /// <summary>定位 node.exe:先 PATH,再 winget/nvm/fnm/chocolatey 路径。</summary>
    private static string? ResolveNode()
    {
        if (_nodeExe != null) return _nodeExe;

        // 1) PATH 中的 node
        var fromPath = TryFindInPath("node");
        if (fromPath != null) { _nodeExe = fromPath; return _nodeExe; }

        // 2) 常见安装路径（含 winget / 官方 msi / 手动 zip）
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new List<string>
        {
            Path.Combine(programFiles,     @"nodejs\node.exe"),
            Path.Combine(programFilesX86,  @"nodejs\node.exe"),
            Path.Combine(localAppData,     @"Programs\nodejs\node.exe"),
            Path.Combine(roamingAppData,   @"..\Local\Programs\nodejs\node.exe"),
        };

        // Chocolatey 默认安装到 C:\tools
        var chocolateyLib = Path.Combine(localAppData, @"..\..\tools\nodejs\node.exe");
        if (File.Exists(chocolateyLib)) candidates.Add(Path.GetFullPath(chocolateyLib));

        // fnm (Fast Node Manager) 默认 %FNM_DIR% 或 ~/.fnm
        var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR")
                     ?? Path.Combine(roamingAppData, @"..\Local\fnm\node-versions");
        TryAddFnmNode(candidates, fnmDir);

        // Volta
        var voltaDir = Environment.GetEnvironmentVariable("VOLTA_HOME")
                       ?? Path.Combine(roamingAppData, @"..\Local\Volta\tools\image\node");
        TryAddDirExe(candidates, voltaDir, "node.exe");

        // 当前用户主目录扫描常见位置
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, "node.exe"));                         // 绿色版
        candidates.Add(Path.Combine(home, "nodejs", "node.exe"));               // 手动 unzip

        foreach (var c in candidates.Where(File.Exists))
        {
            _nodeExe = c;
            return _nodeExe;
        }

        // 3) NVM-Windows: 扫描 %NVM_HOME% 或默认 %APPDATA%\nvm 下 versions 目录找最新版本
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME")
                      ?? Path.Combine(roamingAppData, "nvm");
        TryAddNvmNode(candidates, nvmHome);

        foreach (var c in candidates.Where(File.Exists))
        {
            _nodeExe = c;
            return _nodeExe;
        }

        return null; // 由外层构造友好错误消息
    }

    private static void TryAddFnmNode(List<string> candidates, string fnmDir)
    {
        try
        {
            if (!Directory.Exists(fnmDir)) return;
            var latest = Directory.GetDirectories(fnmDir)
                .Select(d => new { Dir = d, Name = Path.GetFileName(d) ?? "" })
                .Where(x => x.Name.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (latest != null)
                candidates.Add(Path.Combine(latest.Dir, "installation", "node.exe"));
        }
        catch { /* 忽略扫描异常 */ }
    }

    private static void TryAddNvmNode(List<string> candidates, string nvmHome)
    {
        try
        {
            if (!Directory.Exists(nvmHome)) return;
            var versionsDir = Path.Combine(nvmHome, "versions", "node");
            if (!Directory.Exists(versionsDir)) return;
            var latest = Directory.GetDirectories(versionsDir)
                .Select(d => new { Dir = d, Name = Path.GetFileName(d) ?? "" })
                .Where(x => x.Name.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (latest != null)
                candidates.Add(Path.Combine(latest.Dir, "node.exe"));
        }
        catch { /* 忽略 */ }
    }

    private static void TryAddDirExe(List<string> candidates, string dir, string exe)
    {
        try { if (Directory.Exists(dir)) candidates.Add(Path.Combine(dir, exe)); } catch { }
    }

    private static string? TryFindInPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir.Trim('"'), exe + ".exe");
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
