using System.Threading;
using System.Windows;
using SmartVol.Services;
using SmartVol.UI;

namespace SmartVol;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Global\SmartVol_SingleInstance";

    private Mutex? _mutex;
    private HotkeyService? _hotkeys;
    private DeviceChangeWatcher? _deviceWatcher;
    private TrayManager? _tray;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settings;
    private ConfigService? _configService;
    private AppConfig? _config;
    private AudioService? _audio;
    private VolumeController? _controller;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLogger.Info("SmartVol starting up");

        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            System.Windows.MessageBox.Show(
                "SmartVol is already running.\nLook for its icon in the system tray.",
                "SmartVol", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("Dispatcher exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLogger.Error("Process exception: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Background task exception", args.Exception);
            args.SetObserved();
        };

        _configService = new ConfigService();
        _config = _configService.Load();
        _audio = new AudioService();
        _overlay = new OverlayWindow();
        _controller = new VolumeController(_overlay, _audio, _config);

        _hotkeys = new HotkeyService(action =>
            Dispatcher.BeginInvoke(() => _controller.Handle(action)));
        Safe(_hotkeys.Start, "Hotkey start failed");

        _tray = new TrayManager(
            _audio, _config,
            onSettings: () => Dispatcher.BeginInvoke(ShowSettings),
            onFixHotkeys: () => Dispatcher.BeginInvoke(() => Safe(() => _hotkeys?.Restart(), "Hotkey restart failed")),
            onExit: () => Dispatcher.BeginInvoke(ShutdownApp));
        _tray.Start();

        // Must be created on the UI thread (owns a DispatcherTimer).
        _deviceWatcher = new DeviceChangeWatcher(() => Dispatcher.BeginInvoke(() =>
        {
            _tray?.RebuildMenu();
            _settings?.RefreshDevices();
        }));
        _deviceWatcher.Start();

        if (e.Args.Contains("--show-settings", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(ShowSettings);

        if (e.Args.Contains("--selftest-audio", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(RunAudioSelfTest);
    }

    /// <summary>
    /// Hidden diagnostic: exercises device enumeration, default-device
    /// selection (re-selecting the current default = harmless no-op) and a
    /// net-zero master-volume round trip, then exits. Results go to the log.
    /// </summary>
    private void RunAudioSelfTest()
    {
        try
        {
            var devices = _audio!.GetActiveRenderDevices();
            AppLogger.Info($"SELFTEST render devices: {devices.Count}");
            foreach (var device in devices)
                AppLogger.Info($"SELFTEST device: {device.FriendlyName} [{device.Id}]");

            string? currentDefault = _audio.GetDefaultDeviceId();
            AppLogger.Info($"SELFTEST current default: {currentDefault}");
            if (currentDefault != null)
            {
                _audio.SetDefaultDevice(currentDefault);
                AppLogger.Info("SELFTEST re-select current default: OK");
            }

            if (_audio.TryAdjustMasterVolume(-0.05, out double lowered) &&
                _audio.TryAdjustMasterVolume(0.05, out double restored))
            {
                AppLogger.Info($"SELFTEST master volume round trip: OK ({lowered:P0} -> {restored:P0})");
            }
            else
            {
                AppLogger.Error("SELFTEST master volume round trip failed");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("SELFTEST failed", ex);
        }
        finally
        {
            ShutdownApp();
        }
    }

    private void ShowSettings()
    {
        if (_settings != null)
        {
            if (_settings.WindowState == WindowState.Minimized)
                _settings.WindowState = WindowState.Normal;
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(
            _configService!, _config!, _audio!,
            onChanged: () => _tray?.RebuildMenu());
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private static void Safe(Action action, string context)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLogger.Error(context, ex);
        }
    }

    private void ShutdownApp()
    {
        Safe(() => _tray?.Dispose(), "Tray shutdown failed");
        Safe(() => _deviceWatcher?.Dispose(), "Device watcher shutdown failed");
        Safe(() => _hotkeys?.Dispose(), "Hotkey shutdown failed");
        Safe(() => _overlay?.Close(), "Overlay shutdown failed");

        AppLogger.Info("SmartVol shutting down");

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // Already released or not owned — safe to ignore during shutdown.
        }

        _mutex?.Dispose();
        Shutdown();
    }
}
