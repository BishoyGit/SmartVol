using System.Collections.Generic;
using SmartVol.Services;
using SmartVol.UI;

namespace SmartVol;

/// <summary>
/// Bridges hotkey events to audio + overlay. Per-app audio work is serialized
/// off the UI thread so session enumeration cannot stall foreground input.
/// </summary>
public sealed class VolumeController
{
    private readonly OverlayWindow _overlay;
    private readonly AudioService _audio;
    private readonly AppConfig _config;
    private readonly object _appAdjustmentLock = new();
    private readonly LinkedList<AppAdjustmentRequest> _appAdjustmentQueue = new();
    private readonly Dictionary<(int Pid, long StartTimeTicks), System.Windows.Media.ImageSource> _appIcons = new();
    private readonly HashSet<(int Pid, long StartTimeTicks)> _appIconLoadsInProgress = new();
    private bool _appAdjustmentWorkerRunning;

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
        var target = ForegroundAppService.CaptureForegroundWindow();
        if (target == null)
            return;

        lock (_appAdjustmentLock)
        {
            var request = new AppAdjustmentRequest(target, delta);
            var last = _appAdjustmentQueue.Last;
            if (last != null && last.Value.Target.Pid == target.Pid &&
                Math.Sign(last.Value.Delta) == Math.Sign(delta))
            {
                last.Value = request with { Delta = last.Value.Delta + delta };
            }
            else
            {
                _appAdjustmentQueue.AddLast(request);
            }

            if (_appAdjustmentWorkerRunning)
                return;

            _appAdjustmentWorkerRunning = true;
            _ = Task.Run(ProcessAppAdjustments);
        }
    }

    private void ProcessAppAdjustments()
    {
        while (true)
        {
            AppAdjustmentRequest request;
            lock (_appAdjustmentLock)
            {
                if (_appAdjustmentQueue.Count == 0)
                {
                    _appAdjustmentWorkerRunning = false;
                    return;
                }

                request = _appAdjustmentQueue.First!.Value;
                _appAdjustmentQueue.RemoveFirst();
            }

            try
            {
                var foreground = ForegroundAppService.GetForeground(request.Target);
                if (foreground == null)
                    continue;

                bool hasSession = _audio.TryAdjustAppVolume(
                    foreground.Pid, foreground.ProcessName, request.Delta,
                    out double volume, out bool adjusted) && adjusted;
                var processKey = (foreground.Pid, foreground.ProcessStartTimeUtcTicks);
                System.Windows.Media.ImageSource? icon;
                bool requestIcon;
                lock (_appAdjustmentLock)
                {
                    _appIcons.TryGetValue(processKey, out icon);
                    requestIcon = icon == null && _appIconLoadsInProgress.Add(processKey);
                }

                _ = _overlay.Dispatcher.BeginInvoke(() =>
                {
                    if (hasSession)
                    {
                        _overlay.ShowAppVolume(
                            foreground.Pid, foreground.ProcessName, foreground.WindowTitle,
                            icon, volume, _config);
                    }
                    else
                    {
                        _overlay.ShowNoSession(
                            foreground.Pid, foreground.ProcessName, foreground.WindowTitle,
                            icon, _config);
                    }
                });

                if (requestIcon)
                    _ = LoadAndApplyAppIconAsync(foreground);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Asynchronous app volume adjustment failed", ex);
            }
        }
    }

    private async Task LoadAndApplyAppIconAsync(ForegroundApp foreground)
    {
        try
        {
            var icon = await _audio.GetAppIconAsync(
                foreground.Pid, foreground.ProcessStartTimeUtcTicks).ConfigureAwait(false);
            if (icon != null)
            {
                lock (_appAdjustmentLock)
                {
                    _appIcons[(foreground.Pid, foreground.ProcessStartTimeUtcTicks)] = icon;
                }

                _ = _overlay.Dispatcher.BeginInvoke(
                    () => _overlay.SetAppIcon(foreground.Pid, icon));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Asynchronous app icon update failed for pid={foreground.Pid}", ex);
        }
        finally
        {
            lock (_appAdjustmentLock)
            {
                _appIconLoadsInProgress.Remove(
                    (foreground.Pid, foreground.ProcessStartTimeUtcTicks));
            }
        }
    }

    private void AdjustSystem(double delta)
    {
        if (_audio.TryAdjustMasterVolume(delta, out double volume))
            _overlay.ShowSystemVolume(volume, _audio.SystemVolumeIcon, _config);
    }

    private sealed record AppAdjustmentRequest(ForegroundWindowSnapshot Target, double Delta);
}
