using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SmartVol.Services;
using WinForms = System.Windows.Forms;

namespace SmartVol.UI;

public partial class OverlayWindow : Window
{
    private static readonly SolidColorBrush SubtitleBrush = new(System.Windows.Media.Color.FromRgb(0xA1, 0xA1, 0xAA));
    private static readonly SolidColorBrush WarningBrush = new(System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24));

    private readonly DispatcherTimer _hideTimer;
    private bool _fading;
    private int? _currentAppPid;

    static OverlayWindow()
    {
        SubtitleBrush.Freeze();
        WarningBrush.Freeze();
    }

    public OverlayWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) => BeginHide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Keep the overlay out of Alt+Tab without affecting topmost behavior.
        var handle = new WindowInteropHelper(this).Handle;
        int style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_TOOLWINDOW);
    }

    public void ShowAppVolume(int processId, string appName, string windowTitle, ImageSource? icon, double fraction, AppConfig config) =>
        Show(appName, string.IsNullOrWhiteSpace(windowTitle) ? "Per-app volume" : windowTitle,
            icon, fraction, warning: null, config, processId);

    public void ShowNoSession(int processId, string appName, string windowTitle, ImageSource? icon, AppConfig config) =>
        Show(appName, string.IsNullOrWhiteSpace(windowTitle) ? "Per-app volume" : windowTitle,
            icon, 0, "No audio session for this window", config, processId);

    public void ShowSystemVolume(double fraction, ImageSource? icon, AppConfig config) =>
        Show("System Volume", "Global output", icon, fraction, warning: null, config);

    public void SetAppIcon(int processId, ImageSource icon)
    {
        if (IsVisible && _currentAppPid == processId)
            AppIconImg.Source = icon;
    }

    private void Show(
        string title,
        string subtitle,
        ImageSource? icon,
        double fraction,
        string? warning,
        AppConfig config,
        int? processId = null)
    {
        try
        {
            int? previousAppPid = _currentAppPid;
            _currentAppPid = processId;
            if (_fading)
            {
                BeginAnimation(OpacityProperty, null);
                _fading = false;
            }

            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(config.OverlayDurationMs);

            bool wasVisible = IsVisible;
            if (!wasVisible)
            {
                Opacity = 0;
                Show(); // ShowActivated=false: never steals focus (e.g. from games)
            }

            PositionOnActiveMonitor();

            AppNameText.Text = string.IsNullOrWhiteSpace(title) ? "Unknown app" : title;
            SubtitleText.Text = warning ?? subtitle;
            SubtitleText.Foreground = warning != null ? WarningBrush : SubtitleBrush;
            if (icon != null || previousAppPid != processId)
                AppIconImg.Source = icon;

            double clamped = Math.Clamp(fraction, 0, 1);
            PercentText.Text = warning != null ? "—" : ((int)Math.Round(clamped * 100)) + "%";

            double trackWidth = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : 360;
            VolumeFill.Width = Math.Max(0, trackWidth * clamped);

            if (wasVisible)
            {
                Opacity = config.OverlayOpacity;
            }
            else
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(
                    0, config.OverlayOpacity, TimeSpan.FromMilliseconds(120)));
            }

            _hideTimer.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Overlay show failed", ex);
        }
    }

    private void BeginHide()
    {
        _hideTimer.Stop();
        if (!IsVisible || _fading)
            return;

        _fading = true;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            _fading = false;
            try
            {
                Hide();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Overlay hide failed", ex);
            }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Bottom-center of the monitor that owns the foreground window, DPI-aware.
    /// </summary>
    private void PositionOnActiveMonitor()
    {
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            WinForms.Screen screen;
            try
            {
                screen = foreground != IntPtr.Zero
                    ? WinForms.Screen.FromHandle(foreground)
                    : WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
            }
            catch
            {
                screen = WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
            }

            var area = screen.WorkingArea; // physical pixels
            double dx = 1, dy = 1;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var toDip = source.CompositionTarget.TransformFromDevice;
                dx = toDip.M11;
                dy = toDip.M22;
            }

            double workX = area.X * dx;
            double workY = area.Y * dy;
            double workW = area.Width * dx;
            double workH = area.Height * dy;

            Left = workX + ((workW - Width) / 2);
            Top = workY + workH - Height - (110 * dy);
            if (Top < workY)
                Top = workY + ((workH - Height) / 2);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Overlay positioning failed", ex);
        }
    }
}
