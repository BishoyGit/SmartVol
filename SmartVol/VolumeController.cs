using SmartVol.Services;
using SmartVol.UI;

namespace SmartVol;

/// <summary>
/// Bridges hotkey events to audio + overlay. Every event is isolated in its own
/// try/catch so one stale audio session can never break later keypresses.
/// Runs on the UI dispatcher (via BeginInvoke from <see cref="App"/>), so no
/// polling queue or watchdog loop is needed.
/// </summary>
public sealed class VolumeController
{
    private readonly OverlayWindow _overlay;
    private readonly AudioService _audio;
    private readonly AppConfig _config;

    public VolumeController(OverlayWindow overlay, AudioService audio, AppConfig config)
    {
        _overlay = overlay;
        _audio = audio;
        _config = config;
    }

    public void Handle(VolumeAction action)
    {
        try
        {
            double step = _config.Step;
            switch (action)
            {
                case VolumeAction.AppUp:
                    AdjustApp(step);
                    break;
                case VolumeAction.AppDown:
                    AdjustApp(-step);
                    break;
                case VolumeAction.SystemUp:
                    AdjustSystem(step);
                    break;
                default:
                    AdjustSystem(-step);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("VolumeController.Handle failed", ex);
        }
    }

    private void AdjustApp(double delta)
    {
        var foreground = ForegroundAppService.GetForeground();
        if (foreground == null)
            return;

        if (_audio.TryAdjustAppVolume(foreground.Pid, foreground.ProcessName, delta,
                out double volume, out bool hasSession) && hasSession)
        {
            _overlay.ShowAppVolume(
                foreground.ProcessName, foreground.WindowTitle,
                _audio.GetAppIcon(foreground.ExePath), volume, _config);
        }
        else
        {
            _overlay.ShowNoSession(
                foreground.ProcessName, foreground.WindowTitle,
                _audio.GetAppIcon(foreground.ExePath), _config);
        }
    }

    private void AdjustSystem(double delta)
    {
        if (_audio.TryAdjustMasterVolume(delta, out double volume))
            _overlay.ShowSystemVolume(volume, _audio.SystemVolumeIcon, _config);
    }
}
