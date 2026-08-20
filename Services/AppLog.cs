using System;
using System.IO;
using System.Text;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 简易日志系统。写入 %AppData%\WoWSBattleAssistant\app.log，
/// 支持在设置面板查看和导出。
/// </summary>
public static class AppLog
{
    private static readonly object _lock = new();
    private static string? _logPath;

    public static string LogPath
    {
        get
        {
            if (_logPath == null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WoWSBattleAssistant");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "app.log");
            }
            return _logPath;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Debug(string message) => Write("DEBUG", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Write("ERROR", msg);
    }

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch { /* 日志写入失败不影响主流程 */ }
    }

    /// <summary>读取最近 N 行日志</summary>
    public static string ReadTail(int lines = 500)
    {
        try
        {
            if (!File.Exists(LogPath)) return "(暂无日志)";
            var allLines = File.ReadAllLines(LogPath, Encoding.UTF8);
            if (allLines.Length == 0) return "(暂无日志)";
            var start = Math.Max(0, allLines.Length - lines);
            return string.Join(Environment.NewLine, allLines[start..]);
        }
        catch (Exception ex)
        {
            return $"(读取日志失败: {ex.Message})";
        }
    }

    /// <summary>导出完整日志到指定文件</summary>
    public static bool ExportTo(string filePath)
    {
        try
        {
            if (!File.Exists(LogPath)) return false;
            File.Copy(LogPath, filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AppLog] Export failed: {ex.Message}"); return false; }
    }

    /// <summary>清空日志</summary>
    public static void Clear()
    {
        try
        {
            lock (_lock) { File.WriteAllText(LogPath, "", Encoding.UTF8); }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AppLog] Clear failed: {ex.Message}"); }
    }
}
