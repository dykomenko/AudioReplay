using System.Collections.Concurrent;

namespace AudioReplay.Core.Audio;

/// <summary>
/// All live tracks. Capture threads write into it; the replay saver and the continuous recorder read from it.
/// </summary>
public sealed class TrackStore : IAudioSink
{
    private readonly ConcurrentDictionary<string, TrackRing> _rings = new(StringComparer.Ordinal);
    private int _capacityChunks;

    public TrackStore(TimeSpan retention) => SetRetention(retention);

    public void SetRetention(TimeSpan retention)
    {
        // 10 chunks per second plus slack so readers never race the writer on the oldest chunk.
        _capacityChunks = (int)Math.Ceiling(retention.TotalSeconds * 10) + 30;
        foreach (var ring in _rings.Values) ring.Resize(_capacityChunks);
    }

    public void Write(TrackInfo track, long startFrame, ReadOnlySpan<short> samples) =>
        _rings.GetOrAdd(track.Id, _ => new TrackRing(track, _capacityChunks)).Write(startFrame, samples);

    public TrackRing[] SnapshotRings() => [.. _rings.Values];

    public void Trim(long nowFrame)
    {
        var nowChunk = FrameClock.ToChunk(nowFrame);
        foreach (var ring in _rings.Values) ring.Trim(nowChunk);
    }

    public void Clear()
    {
        foreach (var ring in _rings.Values) ring.Clear();
        _rings.Clear();
    }
}
