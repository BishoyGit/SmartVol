using System.Diagnostics;
using System.Text;

namespace SmartVol.Services;

public sealed record ForegroundApp(int Pid, string ProcessName, string WindowTitle);
public sealed record ForegroundWindowSnapshot(IntPtr Handle, int Pid);

internal static class ForegroundAppService
{
    public static ForegroundWindowSnapshot? CaptureForegroundWindow()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return null;

            return new ForegroundWindowSnapshot(hwnd, (int)pid);
        }
        catch (Exception ex)
        {
            AppLogger.Error("CaptureForegroundWindow failed", ex);
            return null;
        }
    }

    public static ForegroundApp? GetForeground(ForegroundWindowSnapshot snapshot)
    {
        string title = GetWindowTitle(snapshot.Handle);

        try
        {
            using var process = Process.GetProcessById(snapshot.Pid);
            string name = process.ProcessName + ".exe";
            return new ForegroundApp(snapshot.Pid, name, title);
        }
        catch
        {
            // Process exited between the foreground query and the lookup.
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
