using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartVol.Services;

public enum VolumeAction
{
    AppUp,
    AppDown,
    SystemUp,
    SystemDown,
}

/// <summary>
/// Low-level keyboard hook that intercepts Right Ctrl + Arrow combinations and
/// suppresses them so the foreground app never sees the keypress.
/// Replaces the Python 'keyboard' package hook (which needed two watchdogs):
/// WH_KEYBOARD_LL is OS-maintained, and a lightweight 3s timer only clears a
/// stuck modifier flag and reinstalls the hook if it ever goes missing.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeyup = 0x0105;
    private const int VkRcontrol = 0xA3;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly Action<VolumeAction> _onAction;
    private readonly System.Threading.Timer _healthTimer;
    private HookProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private volatile bool _rightCtrlDown;
    private bool _disposed;

    public HotkeyService(Action<VolumeAction> onAction)
    {
        _onAction = onAction;
        _healthTimer = new System.Threading.Timer(
            _ => HealthCheck(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        Install();
        _healthTimer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public void Restart()
    {
        AppLogger.Info("Restarting keyboard hook on request");
        Uninstall();
        Install();
    }

    private void Install()
    {
        _proc = HookCallback; // kept alive as a field for the hook's lifetime
        using var process = Process.GetCurrentProcess();
        string? moduleName = null;
        try
        {
            moduleName = process.MainModule?.ModuleName;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not read process module name", ex);
        }

        _hookId = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(moduleName), 0);
        if (_hookId == IntPtr.Zero)
            AppLogger.Error($"SetWindowsHookEx failed (win32={Marshal.GetLastWin32Error()})");
        else
            AppLogger.Info("Keyboard hook installed (Right Ctrl + Arrows)");
    }

    private void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _proc = null;
        _rightCtrlDown = false;
    }

    private void HealthCheck()
    {
        try
        {
            if (_rightCtrlDown && (GetAsyncKeyState(VkRcontrol) & 0x8000) == 0)
                _rightCtrlDown = false; // key-up was missed (focus change) — unstick

            if (_hookId == IntPtr.Zero && !_disposed)
            {
                AppLogger.Error("Keyboard hook missing — reinstalling");
                Install();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Hotkey health check failed", ex);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam);
            bool keyDown = msg == WmKeydown || msg == WmSyskeydown;
            bool keyUp = msg == WmKeyup || msg == WmSyskeyup;

            if (vk == VkRcontrol)
            {
                if (keyDown)
                    _rightCtrlDown = true;
                else if (keyUp)
                    _rightCtrlDown = false;
            }
            else if (keyDown && (vk == VkUp || vk == VkDown || vk == VkLeft || vk == VkRight))
            {
                // Physical-state fallback covers a Ctrl press that landed
                // before the hook was installed.
                bool combo = _rightCtrlDown || (GetAsyncKeyState(VkRcontrol) & 0x8000) != 0;
                if (combo)
                {
                    _rightCtrlDown = true;
                    VolumeAction action = vk switch
                    {
                        VkUp => VolumeAction.AppUp,
                        VkDown => VolumeAction.AppDown,
                        VkRight => VolumeAction.SystemUp,
                        _ => VolumeAction.SystemDown,
                    };

                    try
                    {
                        _onAction(action);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Hotkey action failed", ex);
                    }

                    return (IntPtr)1; // suppress: plain arrows keep working everywhere else
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _healthTimer.Dispose();
        Uninstall();
    }
}
