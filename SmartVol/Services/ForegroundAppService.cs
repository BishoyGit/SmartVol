using System.Diagnostics;
using System.Text;

namespace SmartVol.Services;

public sealed record ForegroundApp(int Pid, string ProcessName, string? ExePath, string WindowTitle);

internal static class ForegroundAppService
{
    public static ForegroundApp? GetForeground()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return null;

            string title = GetWindowTitle(hwnd);

            try
            {
                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName + ".exe";
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Protected / elevated processes hide their module info.
                }

                return new ForegroundApp((int)pid, name, path, title);
            }
            catch
            {
                // Process exited between the foreground query and the lookup.
                return null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("GetForeground failed", ex);
            return null;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(512);
            return NativeMethods.GetWindowText(hwnd, sb, sb.Capacity) > 0
                ? sb.ToString()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
