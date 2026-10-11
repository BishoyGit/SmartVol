using System.IO;
using System.Text;

namespace SmartVol.Services;

/// <summary>
/// Minimal thread-safe rolling file logger (smartvol.log next to the exe,
/// ~1MB with 2 backups). Logging must never crash the app.
/// </summary>
internal static class AppLogger
{
    private static readonly object _lock = new();
    private static readonly string _path = Path.Combine(AppContext.BaseDirectory, "smartvol.log");
    private const long MaxBytes = 1_000_000;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (_lock)
            {
                RotateIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId}] {message}";
                if (ex != null)
                    line += Environment.NewLine + ex;
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes)
                return;

            string bak2 = _path + ".2";
            string bak1 = _path + ".1";
            if (File.Exists(bak2))
                File.Delete(bak2);
            if (File.Exists(bak1))
                File.Move(bak1, bak2);
            File.Move(_path, bak1);
        }
        catch
        {
            // Rotation failure must not block logging; the next write retries.
        }
    }
}
