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

/// <summary>A validated hotkey rule: main key + required modifier VKs.</summary>
public sealed record HotkeyRule(VolumeAction Action, int Key, int[] Modifiers);

/// <summary>
/// Low-level keyboard hook that intercepts user-configured hotkeys and
/// suppresses them so the foreground app never sees the keypress.
/// WH_KEYBOARD_LL is OS-maintained, and a lightweight 3s timer resyncs stuck
/// keys and reinstalls the hook if it ever goes missing.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeyup = 0x0105;

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
    private readonly HashSet<int> _downModifiers = new();
    private readonly object _keysLock = new();
    private Dictionary<int, List<HotkeyRule>> _rulesByKey = new();
    private bool _eitherSide;
    private volatile bool _suspended;
    private HookProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;

    public HotkeyService(Action<VolumeAction> onAction)
    {
        _onAction = onAction;
        _healthTimer = new System.Threading.Timer(
            _ => HealthCheck(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Replaces the active hotkey rules (e.g. after the user saves settings).
    /// When <paramref name="eitherSideModifiers"/> is on, left/right variants
    /// of a modifier count as the same key. Safe to call at any time; only
    /// clears pressed-key tracking.
    /// </summary>
    public void SetBindings(IEnumerable<HotkeyRule> rules, bool eitherSideModifiers)
    {
        var map = new Dictionary<int, List<HotkeyRule>>();
        int count = 0;
        foreach (var rule in rules)
        {
            if (rule.Key == 0 || HotkeyBindings.IsModifierVk(rule.Key))
                continue;
            var modifiers = rule.Modifiers
                .Where(HotkeyBindings.IsModifierVk)
                .Distinct()
                .ToArray();
            var clean = rule with { Modifiers = modifiers };
            if (!map.TryGetValue(clean.Key, out var list))
            {
                list = new List<HotkeyRule>();
                map[clean.Key] = list;
            }

            list.Add(clean);
            count++;
        }

        _rulesByKey = map;
        _eitherSide = eitherSideModifiers;
        lock (_keysLock)
        {
            _downModifiers.Clear();
        }

        AppLogger.Info($"Hotkey bindings updated ({count} rules, either-side={(eitherSideModifiers ? "on" : "off")})");
        foreach (var entry in map)
        {
            foreach (var logged in entry.Value)
            {
                AppLogger.Info($"  rule: {logged.Action} key=0x{logged.Key:X2} mods=[{string.Join(",", logged.Modifiers.Select(m => $"0x{m:X2}"))}]");
            }
        }
    }

    /// <summary>
    /// Temporarily stops hotkeys from firing (used while the user is
    /// capturing a new combination in Settings, so half-pressed combos can't
    /// trigger volume changes mid-capture). The hook stays installed and
    /// modifier tracking continues, so resume is seamless.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        AppLogger.Info(suspended ? "Hotkeys suspended (capturing)" : "Hotkeys resumed");
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
            AppLogger.Info("Keyboard hook installed");
    }

    private void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _proc = null;
        lock (_keysLock)
        {
            _downModifiers.Clear();
        }
    }

    private void HealthCheck()
    {
        try
        {
            // A key-up delivered while focus changed elsewhere leaves a stuck
            // modifier — drop anything not physically down anymore.
            lock (_keysLock)
            {
                _downModifiers.RemoveWhere(vk => (GetAsyncKeyState(vk) & 0x8000) == 0);
            }

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

            if (HotkeyBindings.IsModifierVk(vk))
            {
                lock (_keysLock)
                {
                    if (keyDown)
                        _downModifiers.Add(vk);
                    else if (keyUp)
                        _downModifiers.Remove(vk);
                }
            }
            else if (keyDown && !_suspended && _rulesByKey.TryGetValue(vk, out var rules))
            {
                foreach (var rule in rules)
                {
                    if (!ModifiersMatch(rule.Modifiers))
                        continue;

                    try
                    {
                        _onAction(rule.Action);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Hotkey action failed", ex);
                    }

                    return (IntPtr)1; // suppress: the key never reaches the foreground app
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool ModifiersMatch(int[] required)
    {
        if (_eitherSide)
        {
            var requiredFamilies = new HashSet<int>(
                required.Select(HotkeyBindings.ModifierFamily));

            // Every required family must have at least one side down, and no
            // other family may be down — same exact-match rule, side-blind.
            foreach (int family in requiredFamilies)
            {
                if (!IsFamilyDown(family))
                    return false;
            }

            foreach (int mod in HotkeyBindings.AllModifierVks)
            {
                if (IsDown(mod) && !requiredFamilies.Contains(HotkeyBindings.ModifierFamily(mod)))
                    return false;
            }

            return true;
        }

        // Every required modifier must be down (tracked state with a physical
        // fallback for presses that landed before the hook was installed),
        // and no other modifier may be down — standard exact-match behavior
        // so e.g. Ctrl+Shift+A never fires a Ctrl+A binding by accident.
        foreach (int mod in required)
        {
            if (!IsDown(mod))
                return false;
        }

        foreach (int mod in HotkeyBindings.AllModifierVks)
        {
            if (!required.Contains(mod) && IsDown(mod))
                return false;
        }

        return true;
    }

    private bool IsFamilyDown(int family)
    {
        foreach (int mod in HotkeyBindings.AllModifierVks)
        {
            if (HotkeyBindings.ModifierFamily(mod) == family && IsDown(mod))
                return true;
        }

        return false;
    }

    private bool IsDown(int vk)
    {
        lock (_keysLock)
        {
            if (_downModifiers.Contains(vk))
                return true;
        }

        return (GetAsyncKeyState(vk) & 0x8000) != 0;
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
