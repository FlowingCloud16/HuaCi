using System.IO;

namespace HuaCi.Services;

/// <summary>极简文件日志：%AppData%\HuaCi\log.txt（超过 1MB 自动清空）。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(SettingsService.AppDataDir, "log.txt");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsService.AppDataDir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响程序
        }
    }
}
