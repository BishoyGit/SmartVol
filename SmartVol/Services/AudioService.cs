using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SmartVol.Services;

public sealed record AudioDeviceInfo(string Id, string FriendlyName);

/// <summary>
/// All CoreAudio access (per-app sessions, master volume, device list, icons).
/// Session enumeration skips expired sessions up-front and uses an in-loop
/// name fallback. Per-app icons come from a locked cache that remembers misses.
/// </summary>
public sealed class AudioService
{
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _iconLock = new();
    private ImageSource? _systemIcon;

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    public bool TryAdjustAppVolume(int pid, string? processName, double step, out double newVolume, out bool hasSession)
    {
        newVolume = 0;
        hasSession = false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var sessionManager = device.AudioSessionManager;
            using var sessions = sessionManager.Sessions;

            AudioSessionControl? target = null;
            AudioSessionControl? nameFallback = null;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                uint sessionPid;
                try
                {
                    if (session.State == AudioSessionState.AudioSessionStateExpired)
                        continue;
                    sessionPid = session.GetProcessID;
                }
                catch
                {
                    // Stale session whose owner just exited — skip it.
                    continue;
                }

                if (sessionPid == (uint)pid)
                {
                    target = session;
                    break;
                }

                if (nameFallback == null && !string.IsNullOrEmpty(processName) &&
                    string.Equals(GetSessionProcessName(sessionPid), processName, StringComparison.OrdinalIgnoreCase))
                {
                    nameFallback = session;
                }
            }

            target ??= nameFallback;
            if (target == null)
                return false;

            try
            {
                float current = target.SimpleAudioVolume.Volume;
                float next = Clamp01(current + (float)step);
                target.SimpleAudioVolume.Volume = next;
                newVolume = next;
                hasSession = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to set app volume pid={pid}", ex);
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("TryAdjustAppVolume failed", ex);
            return false;
        }
    }

    public bool TryAdjustMasterVolume(double step, out double newVolume)
    {
        newVolume = 0;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            float current = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            float next = Clamp01(current + (float)step);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = next;
            newVolume = next;
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("TryAdjustMasterVolume failed", ex);
            return false;
        }
    }

    public List<AudioDeviceInfo> GetActiveRenderDevices()
    {
        var result = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    result.Add(new AudioDeviceInfo(device.ID.Trim(), device.FriendlyName));
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Skipping unreadable audio endpoint", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("GetActiveRenderDevices failed", ex);
        }

        return result;
    }

    public string? GetDefaultDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch (Exception ex)
        {
            AppLogger.Error("GetDefaultDeviceId failed", ex);
            return null;
        }
    }

    public void SetDefaultDevice(string deviceId) =>
        DefaultDevicePolicy.SetDefaultEndpoint(deviceId);

    public ImageSource? GetAppIcon(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        lock (_iconLock)
        {
            if (_iconCache.TryGetValue(exePath, out var cached))
                return cached;
        }

        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(48, 48));
                source.Freeze();
                image = source;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"GetAppIcon failed for {exePath}", ex);
        }

        lock (_iconLock)
        {
            _iconCache[exePath] = image;
        }

        return image;
    }

    public ImageSource? SystemVolumeIcon => _systemIcon ??= LoadAppIconResource();

    private static ImageSource? LoadAppIconResource()
    {
        try
        {
            var frame = BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/app.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoadAppIconResource failed", ex);
            return null;
        }
    }

    private static string? GetSessionProcessName(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }
}
