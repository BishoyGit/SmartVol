using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SmartVol.Services;

public sealed record AudioDeviceInfo(string Id, string FriendlyName);

/// <summary>
/// All CoreAudio access (per-app sessions, master volume, and device list).
/// Session enumeration skips expired sessions up-front and uses an in-loop
/// name fallback.
/// </summary>
public sealed class AudioService
{
    private readonly object _appIconLoadLock = new();
    private readonly Dictionary<(int Pid, long StartTimeTicks), Task<ImageSource?>> _appIconLoads = new();
    private readonly Dictionary<string, ImageSource?> _appIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _appIconCacheLock = new();
    private readonly string _appIconCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartVol", "IconCache");
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
            var fallbackSessions = new List<(AudioSessionControl Session, uint Pid)>();

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

                fallbackSessions.Add((session, sessionPid));
            }

            if (target == null && !string.IsNullOrEmpty(processName))
            {
                foreach (var entry in fallbackSessions)
                {
                    if (string.Equals(GetSessionProcessName(entry.Pid), processName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = entry.Session;
                        break;
                    }
                }
            }

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

    public Task<ImageSource?> GetAppIconAsync(int pid, long processStartTimeUtcTicks)
    {
        var processKey = (pid, processStartTimeUtcTicks);
        lock (_appIconLoadLock)
        {
            if (_appIconLoads.TryGetValue(processKey, out var existing))
                return existing;

            var load = Task.Run(() => LoadAppIcon(pid, processStartTimeUtcTicks));
            _appIconLoads.Add(processKey, load);
            _ = load.ContinueWith(
                completed =>
                {
                    lock (_appIconLoadLock)
                    {
                        if (_appIconLoads.TryGetValue(processKey, out var current) &&
                            ReferenceEquals(current, completed))
                        {
                            _appIconLoads.Remove(processKey);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return load;
        }
    }

    private ImageSource? LoadAppIcon(int pid, long expectedStartTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (expectedStartTimeUtcTicks != 0)
            {
                try
                {
                    if (process.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
                        return null;
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to verify process start time for app icon pid={pid}", ex);
                }
            }

            string? exePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
                return null;

            var exe = new FileInfo(exePath);
            string cacheKey = $"{exe.FullName}|{exe.Length}|{exe.LastWriteTimeUtc.Ticks}";
            lock (_appIconCacheLock)
            {
                if (_appIconCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            string cacheFile = Path.Combine(
                _appIconCacheDirectory,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))) + ".png");
            ImageSource? image = File.Exists(cacheFile) ? LoadCachedIcon(cacheFile) : null;
            if (image == null)
            {
                image = ExtractAppIcon(exePath);
                if (image != null)
                    SaveCachedIcon(image, cacheFile);
            }

            lock (_appIconCacheLock)
            {
                _appIconCache[cacheKey] = image;
            }

            return image;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to load app icon for pid={pid}", ex);
            return null;
        }
    }

    private static ImageSource? LoadCachedIcon(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to read cached app icon {path}", ex);
            return null;
        }
    }

    private void SaveCachedIcon(ImageSource image, string path)
    {
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(_appIconCacheDirectory);
            tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create((BitmapSource)image));
                encoder.Save(stream);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to cache app icon {path}", ex);
        }
        finally
        {
            if (tempPath != null && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to remove temporary icon cache file {tempPath}", ex);
                }
            }
        }
    }

    private static ImageSource? ExtractAppIcon(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to extract app icon from {exePath}", ex);
            return null;
        }
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
