using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SmartVol.Services;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SmartVol.UI;

public sealed class DeviceRow : INotifyPropertyChanged
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }

    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible != value)
            {
                _visible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SettingsWindow : Window
{
    private static readonly string[] HotkeyActions =
    [
        HotkeyBindings.AppUp, HotkeyBindings.AppDown,
        HotkeyBindings.SystemUp, HotkeyBindings.SystemDown,
    ];

    private static readonly System.Windows.Media.SolidColorBrush ArmedBrush =
        new(System.Windows.Media.Color.FromRgb(0x6D, 0x28, 0xD9));

    private readonly ConfigService _configService;
    private readonly AppConfig _config;
    private readonly AudioService _audio;
    private readonly Action _onChanged;
    private readonly Action<bool> _onCaptureStateChanged;
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly Dictionary<string, HotkeyBinding> _pendingHotkeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _captureMods = new();
    private bool _loading;
    private bool _pendingEitherSide;
    private string? _captureAction;

    public SettingsWindow(ConfigService configService, AppConfig config, AudioService audio, Action onChanged, Action<bool> onCaptureStateChanged)
    {
        _configService = configService;
        _config = config;
        _audio = audio;
        _onChanged = onChanged;
        _onCaptureStateChanged = onCaptureStateChanged;

        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        PreviewKeyDown += OnCaptureKeyDown;
        PreviewKeyUp += OnCaptureKeyUp;
        Loaded += (_, _) =>
        {
            LoadGeneral();
            LoadHotkeys();
            RefreshDevices();
        };
    }

    private void LoadGeneral()
    {
        _loading = true;
        try
        {
            StepSlider.Value = Math.Clamp(_config.Step * 100, StepSlider.Minimum, StepSlider.Maximum);
            DurationSlider.Value = Math.Clamp(_config.OverlayDurationMs, DurationSlider.Minimum, DurationSlider.Maximum);
            OpacitySlider.Value = Math.Clamp(_config.OverlayOpacity * 100, OpacitySlider.Minimum, OpacitySlider.Maximum);
            StartupCheck.IsChecked = _config.RunAtStartup;
            UpdateLabels();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadHotkeys()
    {
        _loading = true;
        try
        {
            _pendingEitherSide = _config.HotkeyEitherSideModifiers;
            EitherSideCheck.IsChecked = _pendingEitherSide;

            _pendingHotkeys.Clear();
            var defaults = HotkeyBindings.DefaultConfig();
            foreach (string action in HotkeyActions)
            {
                if (_config.Hotkeys.TryGetValue(action, out var binding) &&
                    binding != null && binding.Key != 0 &&
                    !HotkeyBindings.IsModifierVk(binding.Key))
                {
                    _pendingHotkeys[action] = binding.Clone();
                }
                else
                {
                    _pendingHotkeys[action] = defaults[action].Clone();
                }
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateHotkeyButtons();
    }

    private Button GetHotkeyButton(string action) => action switch
    {
        HotkeyBindings.AppUp => AppUpButton,
        HotkeyBindings.AppDown => AppDownButton,
        HotkeyBindings.SystemUp => SystemUpButton,
        HotkeyBindings.SystemDown => SystemDownButton,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private void UpdateHotkeyButtons()
    {
        foreach (string action in HotkeyActions)
        {
            var button = GetHotkeyButton(action);
            if (button == null)
                continue; // InitializeComponent still in progress

            if (_captureAction == action)
            {
                string mods = _captureMods.Count == 0
                    ? string.Empty
                    : string.Join(" + ", _captureMods
                        .OrderBy(HotkeyBindings.ModifierSortOrder)
                        .Select(HotkeyBindings.KeyName)) + " + …";
                button.Content = string.IsNullOrEmpty(mods)
                    ? "Press keys…  (Esc cancels)"
                    : mods + "  (Esc cancels)";
                button.Background = ArmedBrush;
            }
            else
            {
                _pendingHotkeys.TryGetValue(action, out var binding);
                button.Content = HotkeyBindings.ToDisplayString(binding, _pendingEitherSide);
                button.ClearValue(Button.BackgroundProperty);
            }
        }
    }

    private void OnEitherSideChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _pendingEitherSide = EitherSideCheck.IsChecked == true;
        UpdateHotkeyButtons();
    }

    private void OnHotkeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
            return;

        // Click again to cancel an armed capture.
        if (_captureAction == action)
            DisarmCapture();
        else
            ArmCapture(action);
    }

    private void ArmCapture(string action)
    {
        _captureAction = action;
        _captureMods.Clear();

        // Seed modifiers already held down (e.g. user holds Ctrl with one
        // hand and clicks Change with the other). Without this, a pre-held
        // modifier would be silently dropped and the stored combo would be
        // wrong (e.g. bare "←" instead of "Ctrl + ←").
        foreach (var (key, vk) in new[]
                 {
                     (Key.LeftCtrl, HotkeyBindings.VkLcontrol),
                     (Key.RightCtrl, HotkeyBindings.VkRcontrol),
                     (Key.LeftShift, HotkeyBindings.VkLshift),
                     (Key.RightShift, HotkeyBindings.VkRshift),
                     (Key.LeftAlt, HotkeyBindings.VkLmenu),
                     (Key.RightAlt, HotkeyBindings.VkRmenu),
                     (Key.LWin, HotkeyBindings.VkLwin),
                     (Key.RWin, HotkeyBindings.VkRwin),
                 })
        {
            try
            {
                if (Keyboard.IsKeyDown(key))
                    _captureMods.Add(vk);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Capture modifier seed failed", ex);
            }
        }

        SafeSetSuspended(true);
        UpdateHotkeyButtons();
    }

    private void DisarmCapture()
    {
        _captureAction = null;
        _captureMods.Clear();
        SafeSetSuspended(false);
        UpdateHotkeyButtons();
    }

    private void SafeSetSuspended(bool suspended)
    {
        try
        {
            _onCaptureStateChanged(suspended);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Capture suspend toggle failed", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Never leave hotkeys suspended if the window is closed mid-capture.
        SafeSetSuspended(false);
        base.OnClosed(e);
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (_captureAction == null || e.Key == Key.None || e.Key == Key.ImeProcessed)
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None)
            return;

        if (key == Key.Escape)
        {
            DisarmCapture();
            e.Handled = true;
            return;
        }

        int vk;
        try
        {
            vk = KeyInterop.VirtualKeyFromKey(key);
        }
        catch
        {
            return;
        }

        if (HotkeyBindings.IsModifierVk(vk))
        {
            _captureMods.Add(vk);
            UpdateHotkeyButtons();
            e.Handled = true;
            return;
        }

        if (vk == 0 || e.IsRepeat)
        {
            e.Handled = true;
            return;
        }

        var binding = new HotkeyBinding
        {
            Modifiers = _captureMods
                .OrderBy(HotkeyBindings.ModifierSortOrder)
                .ToList(),
            Key = vk,
        };

        foreach (var (name, existing) in _pendingHotkeys)
        {
            if (name != _captureAction && HotkeyBindings.SameBinding(existing, binding))
            {
                System.Windows.MessageBox.Show(
                    $"That combination is already used for “{HotkeyBindings.DisplayActionName(name)}”.\nPick a different one.",
                    "SmartVol", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
                return;
            }
        }

        _pendingHotkeys[_captureAction] = binding;
        AppLogger.Info($"Hotkey captured for {_captureAction}: {HotkeyBindings.ToDisplayString(binding, _pendingEitherSide)}");
        DisarmCapture();
        e.Handled = true;
    }

    private void OnCaptureKeyUp(object sender, KeyEventArgs e)
    {
        if (_captureAction == null)
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk;
        try
        {
            vk = KeyInterop.VirtualKeyFromKey(key);
        }
        catch
        {
            return;
        }

        if (HotkeyBindings.IsModifierVk(vk) && _captureMods.Remove(vk))
        {
            UpdateHotkeyButtons();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Re-enumerates output devices. Called when the window opens and when
    /// the OS reports endpoint changes while the window is open.
    /// </summary>
    public void RefreshDevices()
    {
        _loading = true;
        try
        {
            _devices.Clear();

            List<AudioDeviceInfo> devices;
            string? defaultId;
            try
            {
                devices = _audio.GetActiveRenderDevices();
                defaultId = _audio.GetDefaultDeviceId();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Settings device enumeration failed", ex);
                return;
            }

            var hidden = new HashSet<string>(_config.HiddenDevices, StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices)
            {
                _devices.Add(new DeviceRow
                {
                    Id = device.Id,
                    Name = device.FriendlyName,
                    IsDefault = string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase),
                    Visible = !hidden.Contains(device.Id.Trim()),
                });
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateLabels()
    {
        // ValueChanged fires while InitializeComponent() is still assigning
        // properties, before the label fields exist — ignore those early events.
        if (StepValueText == null || DurationValueText == null || OpacityValueText == null)
            return;

        StepValueText.Text = $"{(int)StepSlider.Value}% per press";
        DurationValueText.Text = $"{(int)DurationSlider.Value} ms";
        OpacityValueText.Text = $"{(int)OpacitySlider.Value}%";
    }

    private void OnStepChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void OnDurationChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void OnDeviceToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        try
        {
            _config.HiddenDevices = _devices
                .Where(d => !d.Visible && !string.IsNullOrWhiteSpace(d.Id))
                .Select(d => d.Id.Trim())
                .ToList();
            _configService.Save(_config);
            _onChanged();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Device visibility save failed", ex);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            DisarmCapture();

            _config.Step = StepSlider.Value / 100.0;
            _config.OverlayDurationMs = (int)DurationSlider.Value;
            _config.OverlayOpacity = OpacitySlider.Value / 100.0;
            _config.RunAtStartup = StartupCheck.IsChecked == true;
            _config.HotkeyEitherSideModifiers = _pendingEitherSide;
            _config.Hotkeys = _pendingHotkeys.ToDictionary(
                kv => kv.Key, kv => kv.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);

            StartupService.SetEnabled(_config.RunAtStartup);
            _configService.Save(_config);
            _onChanged();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Settings save failed", ex);
        }

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DisarmCapture();
        Close();
    }
}
