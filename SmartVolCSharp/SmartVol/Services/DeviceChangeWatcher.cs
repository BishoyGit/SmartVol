using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace SmartVol.Services;

/// <summary>
/// Listens for audio endpoint arrivals, removals, state changes and
/// default-device changes, then raises a debounced callback on the UI thread
/// so the tray menu (and the settings device list) always matches the real
/// device set. Without this, newly connected devices only showed up in
/// Settings (which re-enumerates on open) but never in the tray menu.
///
/// Must be constructed on the UI thread (the debounce timer is a
/// DispatcherTimer). Callbacks from CoreAudio arrive on a worker thread;
/// only the timer restart happens there — the actual refresh runs on the UI.
/// </summary>
internal sealed class DeviceChangeWatcher : IDisposable
{
    private readonly Action _onDevicesChanged;
    private readonly DispatcherTimer _debounce;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _client;
    private bool _disposed;

    public DeviceChangeWatcher(Action onDevicesChanged)
    {
        _onDevicesChanged = onDevicesChanged;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            try
            {
                _onDevicesChanged();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Device change handler failed", ex);
            }
        };
    }

    public void Start()
    {
        try
        {
            // Both must stay alive for the app's lifetime: disposing either
            // unregisters the callback.
            _enumerator = new MMDeviceEnumerator();
            _client = _enumerator.CreateNotificationClient();

            _client.DeviceAdded += (_, _) => Notify("DeviceAdded");
            _client.DeviceRemoved += (_, _) => Notify("DeviceRemoved");
            _client.DeviceStateChanged += (_, _) => Notify("DeviceStateChanged");
            _client.DefaultDeviceChanged += (_, _) => Notify("DefaultDeviceChanged");

            AppLogger.Info("Device change watcher started");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Device change watcher start failed", ex);
        }
    }

    private void Notify(string what)
    {
        try
        {
            AppLogger.Info($"Audio endpoint change: {what}");
            // Bursts are normal (one plug-in fires Added + StateChanged +
            // PropertyValueChanged + DefaultDeviceChanged); the timer
            // collapses them into a single refresh.
            _debounce.Stop();
            _debounce.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Device change notify failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _debounce.Stop();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Device watcher timer stop failed", ex);
        }

        try
        {
            _client?.Dispose();
            _client = null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Device watcher client dispose failed", ex);
        }

        try
        {
            _enumerator?.Dispose();
            _enumerator = null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Device watcher enumerator dispose failed", ex);
        }
    }
}
