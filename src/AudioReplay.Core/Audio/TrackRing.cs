namespace AudioReplay.Core.Audio;

/// <summary>
/// In-memory circular store for one track, organised as 100 ms chunks addressed by absolute chunk id.
/// A chunk that never received a non-zero sample is <c>null</c>, so silence costs nothing - an app that only
/// beeps once an hour occupies a few kilobytes, not minutes of zeros. Writes are summed (saturating), which
/// lets several processes of one application share a track.
/// </summary>
public sealed class TrackRing
{
    private readonly object _gate = new();
    private readonly Stack<short[]> _pool = new();
    private short[]?[] _chunks;
    private long[] _ids;

    public TrackRing(TrackInfo track, int capacityChunks)
    {
        Track = track;
        Capacity = Math.Max(2, capacityChunks);
        _chunks = new short[Capacity][];
        _ids = new long[Capacity];
        Array.Fill(_ids, -1L);
    }

    public TrackInfo Track { get; }

    public int Capacity { get; private set; }

    public void Write(long startFrame, ReadOnlySpan<short> samples)
    {
        var frames = samples.Length / AudioFormat.Channels;
        var done = 0;
        while (done < frames)
        {
            var frame = startFrame + done;
            var off = (int)(frame % AudioFormat.ChunkFrames);
            var take = Math.Min(frames - done, AudioFormat.ChunkFrames - off);
            var part = samples.Slice(done * AudioFormat.Channels, take * AudioFormat.Channels);

            if (frame >= 0 && AudioMath.HasSignal(part))
            {
                lock (_gate) MixInto(frame / AudioFormat.ChunkFrames, off, part);
            }

            done += take;
        }
    }

    /// <summary>Copies a chunk into <paramref name="dest"/> (ChunkSamples long). False if the chunk is silent/absent.</summary>
    public bool TryCopyChunk(long chunkId, Span<short> dest)
    {
        lock (_gate)
        {
            var slot = SlotOf(chunkId);
            if (_ids[slot] != chunkId || _chunks[slot] is not { } chunk) return false;
            chunk.AsSpan().CopyTo(dest);
            return true;
        }
    }

    /// <summary>Copies chunks [first..last] (inclusive); silent chunks are null.</summary>
    public short[]?[] SnapshotRange(long firstChunk, long lastChunk)
    {
        var count = checked((int)(lastChunk - firstChunk + 1));
        var result = new short[count][];
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                var id = firstChunk + i;
                var slot = SlotOf(id);
                if (_ids[slot] == id && _chunks[slot] is { } chunk) result[i] = (short[])chunk.Clone();
            }
        }

        return result;
    }

    /// <summary>Releases chunks that have fallen out of the retention window.</summary>
    public void Trim(long nowChunk)
    {
        lock (_gate)
        {
            var oldest = nowChunk - Capacity + 1;
            for (var i = 0; i < _chunks.Length; i++)
            {
                if (_chunks[i] is not null && _ids[i] < oldest) Recycle(i);
            }
        }
    }

    public void Resize(int newCapacity)
    {
        newCapacity = Math.Max(2, newCapacity);
        lock (_gate)
        {
            if (newCapacity == Capacity) return;

            var chunks = new short[newCapacity][];
            var ids = new long[newCapacity];
            Array.Fill(ids, -1L);

            // Keep the newest chunks that still fit.
            for (var i = 0; i < _chunks.Length; i++)
            {
                if (_chunks[i] is not { } chunk) continue;
                var slot = (int)(_ids[i] % newCapacity);
                if (chunks[slot] is null || ids[slot] < _ids[i])
                {
                    chunks[slot] = chunk;
                    ids[slot] = _ids[i];
                }
            }

            _chunks = chunks;
            _ids = ids;
            Capacity = newCapacity;
            _pool.Clear();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            for (var i = 0; i < _chunks.Length; i++) Recycle(i);
        }
    }

    private void MixInto(long chunkId, int frameOffset, ReadOnlySpan<short> part)
    {
        var slot = SlotOf(chunkId);
        if (_ids[slot] > chunkId) return; // a newer chunk already owns this slot; the write is stale

        if (_ids[slot] != chunkId)
        {
            Recycle(slot);
            _ids[slot] = chunkId;
        }

        var chunk = _chunks[slot] ??= Rent();
        AudioMath.AddSaturated(chunk.AsSpan(frameOffset * AudioFormat.Channels, part.Length), part);
    }

    private short[] Rent() => _pool.Count > 0 ? _pool.Pop() : new short[AudioFormat.ChunkSamples];

    private void Recycle(int slot)
    {
        if (_chunks[slot] is { } chunk)
        {
            Array.Clear(chunk);
            if (_pool.Count < 64) _pool.Push(chunk);
            _chunks[slot] = null;
        }
    }

    private int SlotOf(long chunkId) => (int)(chunkId % Capacity);
}
