using AudioReplay.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioReplay.Core.Capture;

/// <summary>A microphone (any capture endpoint) opened in shared mode; Windows converts to 48 kHz/16-bit/stereo.</summary>
internal sealed class MicrophoneReader : IPacketSource
{
    private readonly AutoResetEvent _event = new(false);
    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;

    public MicrophoneReader(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(deviceId);

        _client = _device.AudioClient;
        _client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            1_000_000, // 100 ms
            0,
            new WaveFormat(AudioFormat.SampleRate, AudioFormat.BitsPerSample, AudioFormat.Channels),
            Guid.Empty);
        _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient;
    }

    public WaitHandle Signal => _event;

    /// <summary>Enumerates active capture devices as (id, friendly name).</summary>
    public static List<(string Id, string Name)> ListDevices()
    {
        var list = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device) list.Add((device.ID, device.FriendlyName));
        }

        return list;
    }

    public void Start() => _client.Start();

    public void Stop() => _client.Stop();

    public void Drain(PacketHandler handler)
    {
        while (_capture.GetNextPacketSize() > 0)
        {
            var data = _capture.GetBuffer(out var frames, out var flags);
            if (frames == 0) return;

            try
            {
                // NAudio does not expose the packet's QPC time, so the worker falls back to arrival time.
                handler(data, frames, (PacketFlags)(int)flags | PacketFlags.TimestampError, 0);
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
    }

    public void Dispose()
    {
        try { _client.Stop(); } catch { /* ignore */ }
        _capture.Dispose();
        _client.Dispose();
        _device.Dispose();
        _event.Dispose();
    }
}
