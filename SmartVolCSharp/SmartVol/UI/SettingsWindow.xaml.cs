using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using SmartVol.Services;

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
    private readonly ConfigService _configService;
    private readonly AppConfig _config;
    private readonly AudioService _audio;
    private readonly Action _onChanged;
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private bool _loading;

    public SettingsWindow(ConfigService configService, AppConfig config, AudioService audio, Action onChanged)
    {
        _configService = configService;
        _config = config;
        _audio = audio;
        _onChanged = onChanged;

        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        Loaded += (_, _) =>
        {
            LoadGeneral();
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
            _config.Step = StepSlider.Value / 100.0;
            _config.OverlayDurationMs = (int)DurationSlider.Value;
            _config.OverlayOpacity = OpacitySlider.Value / 100.0;
            _config.RunAtStartup = StartupCheck.IsChecked == true;

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

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
