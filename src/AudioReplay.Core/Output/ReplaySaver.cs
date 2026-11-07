using AudioReplay.Core.Audio;
using AudioReplay.Core.Diagnostics;

namespace AudioReplay.Core.Output;

/// <summary>Writes "the last N seconds" of every track that had sound into a new folder, one WAV per app.</summary>
public sealed class ReplaySaver
{
    /// <summary>Packets still in flight when the hotkey is pressed get a moment to land in the buffer.</summary>
    private const int SettleMilliseconds = 150;

    private readonly TrackStore _store;

    public ReplaySaver(TrackStore store) => _store = store;

    public async Task<SaveResult> SaveAsync(int seconds, string saveDirectory, bool writeMix, CancellationToken ct = default)
    {
        var endFrame = FrameClock.NowFrame;
        var stamp = DateTime.Now;
        await Task.Delay(SettleMilliseconds, ct).ConfigureAwait(false);
        return await Task.Run(() => SaveCore(endFrame, stamp, seconds, saveDirectory, writeMix), ct).ConfigureAwait(false);
    }

    private SaveResult SaveCore(long endFrame, DateTime stamp, int seconds, string saveDirectory, bool writeMix)
    {
        var startFrame = Math.Max(0, endFrame - (long)seconds * AudioFormat.SampleRate);
        var totalFrames = endFrame - startFrame;
        if (totalFrames <= 0) return SaveResult.Fail("Буфер пуст.");

        var firstChunk = startFrame / AudioFormat.ChunkFrames;
        var lastChunk = (endFrame - 1) / AudioFormat.ChunkFrames;

        var snapshots = new List<(TrackInfo Track, short[]?[] Chunks)>();
        foreach (var ring in _store.SnapshotRings())
        {
            var chunks = ring.SnapshotRange(firstChunk, lastChunk);
            if (chunks.Any(c => c is not null)) snapshots.Add((ring.Track, chunks));
        }

        if (snapshots.Count == 0) return SaveResult.Fail("В буфере нет звука.");
        snapshots.Sort((a, b) => a.Track.Slot.CompareTo(b.Track.Slot));

        string? folder = null;
        var writers = new List<WavFileWriter>();
        WavFileWriter? mixWriter = null;
        try
        {
            var durationSeconds = (int)Math.Round(totalFrames / (double)AudioFormat.SampleRate);
            folder = FileNaming.CreateUniqueFolder(saveDirectory, $"{stamp:yyyy-MM-dd_HH-mm-ss}_replay_{durationSeconds}s");

            foreach (var (track, _) in snapshots)
                writers.Add(new WavFileWriter(Path.Combine(folder, FileNaming.TrackFileName(track))));
            if (writeMix) mixWriter = new WavFileWriter(Path.Combine(folder, FileNaming.MixFileName));

            var mix = new int[AudioFormat.ChunkSamples];
            var mixOut = new short[AudioFormat.ChunkSamples];

            for (var chunk = firstChunk; chunk <= lastChunk; chunk++)
            {
                var chunkStart = chunk * AudioFormat.ChunkFrames;
                var from = Math.Max(startFrame, chunkStart);
                var to = Math.Min(endFrame, chunkStart + AudioFormat.ChunkFrames);
                var frames = (int)(to - from);
                var sampleOffset = (int)(from - chunkStart) * AudioFormat.Channels;
                var relativeFrame = from - startFrame;
                var any = false;

                if (writeMix) Array.Clear(mix, 0, frames * AudioFormat.Channels);

                for (var i = 0; i < snapshots.Count; i++)
                {
                    if (snapshots[i].Chunks[chunk - firstChunk] is not { } data) continue;

                    var slice = data.AsSpan(sampleOffset, frames * AudioFormat.Channels);
                    writers[i].WriteAtFrame(relativeFrame, slice);
                    if (writeMix) AudioMath.MixInto(mix, slice);
                    any = true;
                }

                if (writeMix && any)
                {
                    AudioMath.ClampTo(mix.AsSpan(0, frames * AudioFormat.Channels), mixOut.AsSpan(0, frames * AudioFormat.Channels));
                    mixWriter!.WriteAtFrame(relativeFrame, mixOut.AsSpan(0, frames * AudioFormat.Channels));
                }
            }

            foreach (var writer in writers) writer.Complete(totalFrames);
            mixWriter?.Complete(totalFrames);

            Log.Info($"replay saved: {folder} ({snapshots.Count} tracks, {durationSeconds}s)");
            return new SaveResult(true, folder, snapshots.Count, TimeSpan.FromSeconds(totalFrames / (double)AudioFormat.SampleRate));
        }
        catch (Exception ex)
        {
            Log.Error("saving replay failed", ex);
            foreach (var writer in writers) writer.Dispose();
            mixWriter?.Dispose();
            return SaveResult.Fail($"Не удалось сохранить: {ex.Message}");
        }
    }
}
