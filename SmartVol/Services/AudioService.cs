using System.Runtime.InteropServices;
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
