using System.Runtime.InteropServices;
using AudioReplay.Core.Audio;
using AudioReplay.Core.Diagnostics;

namespace AudioReplay.Core.Capture;

/// <summary>
/// Pumps one <see cref="IPacketSource"/> on its own thread and places every packet on the shared
/// <see cref="FrameClock"/> timeline. Gaps (the engine sends nothing while a stream is silent) simply stay
/// empty, which keeps all tracks time-aligned.
/// </summary>
internal sealed class CaptureWorker : IDisposable
{
    private const long ResyncFrames = AudioFormat.SampleRate / 10; // 100 ms

    private readonly IPacketSource _source;
    private readonly TrackInfo _track;
    private readonly IAudioSink _sink;
    private readonly string _label;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _faulted;
    private short[] _buffer = new short[AudioFormat.ChunkSamples];
    private long _next = long.MinValue;
    private bool _loggedFirstPacket;

    public CaptureWorker(string label, TrackInfo track, IPacketSource source, IAudioSink sink)
    {
        _label = label;
        _track = track;
        _source = source;
        _sink = sink;
    }

    /// <summary>True if the stream broke (process exited, device removed, ...).</summary>
    public bool Faulted => _faulted;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = $"capture:{_label}" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _source.Dispose();
    }

    private void Run()
    {
        try
        {
            _source.Start();
            while (!_stop)
            {
                _source.Signal.WaitOne(100);
                if (_stop) break;
                _source.Drain(OnPacket);
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            Log.Warn($"capture '{_label}' stopped: {ex.Message}");
        }
        finally
        {
            try { _source.Stop(); } catch { /* ignore */ }
        }
    }

    private void OnPacket(IntPtr data, int frames, PacketFlags flags, long qpc100ns)
    {
        if (frames <= 0) return;

        var now = FrameClock.NowFrame;
        var qpcValid = (flags & PacketFlags.TimestampError) == 0 && qpc100ns > 0;
        long expected;

        if (qpcValid)
        {
            expected = FrameClock.Qpc100nsToFrame(qpc100ns);
            if (_next == long.MinValue || Math.Abs(_next - expected) > ResyncFrames) _next = expected;
        }
        else
        {
            // No usable timestamp: assume the packet just ended. Only ever move forward so several packets
            // drained in one wake-up are not stacked on top of each other.
            expected = now - frames;
            if (_next == long.MinValue || _next < expected - ResyncFrames || _next > now + AudioFormat.SampleRate)
                _next = expected;
        }

        if (!_loggedFirstPacket)
        {
            _loggedFirstPacket = true;
            var lagMs = (now - expected) * 1000.0 / AudioFormat.SampleRate;
            Log.Debug($"capture '{_label}': first packet, {frames} frames, timestamp {(qpcValid ? "valid" : "n/a")}, lag {lagMs:F0} ms");
        }

        if ((flags & PacketFlags.Silent) == 0)
        {
            var samples = frames * AudioFormat.Channels;
            if (_buffer.Length < samples) _buffer = new short[samples];
            Marshal.Copy(data, _buffer, 0, samples);
            _sink.Write(_track, _next, _buffer.AsSpan(0, samples));
        }

        _next += frames;
    }
}
