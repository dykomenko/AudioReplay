using System.Diagnostics;

namespace AudioReplay.Core.Audio;

/// <summary>
/// One shared timeline for every track. A "frame" index is the number of 48 kHz sample frames since
/// <see cref="Origin"/>; it is derived from the performance counter, which is also what WASAPI uses for
/// packet timestamps. Because every source is placed on this timeline, tracks stay aligned with each other.
/// </summary>
public static class FrameClock
{
    // The origin is pushed one hour into the past so frame indexes never go negative, even for packets
    // that were time-stamped slightly before start-up.
    private static readonly long Origin = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3600L;

    public static long NowFrame => TicksToFrame(Stopwatch.GetTimestamp());

    public static long TicksToFrame(long qpcTicks) =>
        (long)((qpcTicks - Origin) * ((double)AudioFormat.SampleRate / Stopwatch.Frequency));

    /// <summary>Converts a WASAPI QPC position (100 ns units) to a frame index.</summary>
    public static long Qpc100nsToFrame(long qpc100ns)
    {
        var ticks = (long)(qpc100ns * (Stopwatch.Frequency / 10_000_000.0));
        return TicksToFrame(ticks);
    }

    public static long ToChunk(long frame) => frame / AudioFormat.ChunkFrames;
}
