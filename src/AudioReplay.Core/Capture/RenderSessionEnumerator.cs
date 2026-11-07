using AudioReplay.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioReplay.Core.Capture;

/// <summary>
/// Lists the audio sessions (one per app per output device) through the public session-manager API.
/// Output devices are cached and only re-enumerated every few seconds; each poll just refreshes the session
/// list, which keeps the idle CPU cost of the watcher negligible.
/// </summary>
internal sealed class RenderSessionEnumerator : IDisposable
{
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly List<MMDevice> _devices = [];
    private DateTime _devicesLoadedAt = DateTime.MinValue;

    public List<SessionInfo> Enumerate()
    {
        if (DateTime.UtcNow - _devicesLoadedAt > DeviceRefreshInterval) ReloadDevices();

        var byPid = new Dictionary<uint, bool>();
        var failed = false;

        foreach (var device in _devices)
        {
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.IsSystemSoundsSession) continue;

                    var pid = session.GetProcessID;
                    var active = session.State == AudioSessionState.AudioSessionStateActive;
                    byPid[pid] = byPid.GetValueOrDefault(pid) || active;
                }
            }
            catch (Exception ex)
            {
                failed = true;
                Log.Debug($"cannot read sessions of a device: {ex.Message}");
            }
        }

        // A device was probably unplugged or changed: pick up the new device list on the next poll.
        if (failed) _devicesLoadedAt = DateTime.MinValue;

        return byPid.Select(kv => new SessionInfo(kv.Key, kv.Value)).ToList();
    }

    public void Dispose() => DisposeDevices();

    private void ReloadDevices()
    {
        DisposeDevices();
        _devicesLoadedAt = DateTime.UtcNow;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _devices.AddRange(enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot enumerate output devices: {ex.Message}");
        }
    }

    private void DisposeDevices()
    {
        foreach (var device in _devices)
        {
            try { device.Dispose(); } catch { /* ignore */ }
        }

        _devices.Clear();
    }
}
