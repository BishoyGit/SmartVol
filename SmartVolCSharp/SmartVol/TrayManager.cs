using System.Drawing;
using System.Windows.Forms;
using SmartVol.Services;

namespace SmartVol;

/// <summary>
/// System-tray icon with output-device switching, hotkey repair, settings and exit.
/// Fix vs the Python version: refreshing the device list rebuilds only the menu
/// (atomic swap) instead of destroying and recreating the whole tray icon, so the
/// icon can never disappear or duplicate after a device change.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly AudioService _audio;
    private readonly AppConfig _config;
    private readonly Action _onSettings;
    private readonly Action _onFixHotkeys;
    private readonly Action _onExit;

    private NotifyIcon? _notify;
    private bool _disposed;

    public TrayManager(
        AudioService audio,
        AppConfig config,
        Action onSettings,
        Action onFixHotkeys,
        Action onExit)
    {
        _audio = audio;
        _config = config;
        _onSettings = onSettings;
        _onFixHotkeys = onFixHotkeys;
        _onExit = onExit;
    }

    public void Start()
    {
        _notify = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "SmartVol — Right Ctrl + Arrows",
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => Safe(_onSettings);
        RebuildMenu();
        AppLogger.Info("Tray icon started");
    }

    public void RebuildMenu()
    {
        var notify = _notify;
        if (notify == null || notify.IsDisposedOrNull())
            return;

        List<AudioDeviceInfo> devices = [];
        string? defaultId = null;
        try
        {
            devices = _audio.GetActiveRenderDevices();
            defaultId = _audio.GetDefaultDeviceId();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tray device enumeration failed", ex);
        }

        // Reload persisted prefs so the menu always matches what is on disk.
        var hidden = new HashSet<string>(_config.HiddenDevices, StringComparer.OrdinalIgnoreCase);
        var visible = devices.Where(d => !hidden.Contains(d.Id.Trim())).ToList();

        var menu = new ContextMenuStrip();

        if (visible.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("(no devices found)") { Enabled = false });
        }
        else
        {
            foreach (var device in visible)
            {
                bool isDefault = string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(
                    (isDefault ? "✓  " : "     ") + device.FriendlyName)
                {
                    Checked = isDefault,
                };
                string id = device.Id;
                string name = device.FriendlyName;
                item.Click += (_, _) => SwitchDevice(id, name);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        AddMenuItem(menu, "Fix Hotkeys", _onFixHotkeys);
        AddMenuItem(menu, "Settings", _onSettings);
        AddMenuItem(menu, "Exit", _onExit);

        var previous = notify.ContextMenuStrip;
        notify.ContextMenuStrip = menu;
        previous?.Dispose();

        AppLogger.Info($"Tray menu rebuilt ({visible.Count} visible of {devices.Count} devices)");
    }

    private void SwitchDevice(string id, string name)
    {
        var notify = _notify;
        try
        {
            _audio.SetDefaultDevice(id);
            notify?.ShowBalloonTip(1500, "SmartVol", $"Output switched to {name}.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to switch default device to {id}", ex);
            notify?.ShowBalloonTip(2500, "SmartVol", "Could not switch the output device.", ToolTipIcon.Error);
        }

        RebuildMenu();
    }

    private void AddMenuItem(ContextMenuStrip menu, string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => Safe(action);
        menu.Items.Add(item);
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tray action failed", ex);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource?.Stream != null)
                return new Icon(resource.Stream);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tray icon load failed", ex);
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (_notify != null)
            {
                _notify.Visible = false;
                _notify.Dispose();
                _notify = null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tray dispose failed", ex);
        }
    }
}

internal static class NotifyIconExtensions
{
    public static bool IsDisposedOrNull(this NotifyIcon? notify)
    {
        if (notify == null)
            return true;
        try
        {
            _ = notify.Text;
            return false;
        }
        catch
        {
            return true;
        }
    }
}
