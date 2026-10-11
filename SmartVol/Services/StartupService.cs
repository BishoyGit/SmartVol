using Microsoft.Win32;

namespace SmartVol.Services;

internal static class StartupService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SmartVol";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Run-at-startup check failed", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
                return;

            if (enabled)
            {
                string exe = Environment.ProcessPath
                    ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? string.Empty;
                if (string.IsNullOrEmpty(exe))
                    return;
                key.SetValue(ValueName, $"\"{exe}\"");
                AppLogger.Info("Run at startup enabled");
            }
            else
            {
                key.DeleteValue(ValueName, false);
                AppLogger.Info("Run at startup disabled");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Run-at-startup update failed", ex);
        }
    }
}
