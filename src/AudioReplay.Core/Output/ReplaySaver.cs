using AudioReplay.Core.Audio;
using AudioReplay.Core.Diagnostics;

namespace AudioReplay.Core.Output;

/// <summary>
/// Writes "the last N seconds" of every track that had sound into a folder, one WAV per app.
/// If the user saves again soon after (merge window), the audio recorded since the previous save is appended to
/// the previous clip instead of starting a new one: all tracks live on one timeline, so the join is exact.
/// </summary>
public sealed class ReplaySaver
{
    /// <summary>Packets still in flight when the hotkey is pressed get a moment to land in the buffer.</summary>
    private const int SettleMilliseconds = 150;

    private readonly TrackStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClipState? _last;

    public ReplaySaver(TrackStore store) => _store = store;

    /// <param name="mergeWindowSeconds">
    /// Append to the previous clip if the previous save ended at most this long ago (capped by the buffer length;
    /// 0 = always create a new clip).
    /// </param>
    public async Task<SaveResult> SaveAsync(int seconds, string saveDirectory, bool writeMix, int mergeWindowSeconds = 0, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var endFrame = FrameClock.NowFrame;
            var stamp = DateTime.Now;
            await Task.Delay(SettleMilliseconds, ct).ConfigureAwait(false);
            return await Task.Run(() => SaveCore(endFrame, stamp, seconds, saveDirectory, writeMix, mergeWindowSeconds), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SaveResult SaveCore(long endFrame, DateTime stamp, int seconds, string saveDirectory, bool writeMix, int mergeWindowSeconds)
    {
        string? note = null;

        if (PlanAppend(endFrame, seconds, mergeWindowSeconds) is { } previous)
        {
            try
            {
                return WriteSegment(previous.Folder, previous.StartFrame, previous.EndFrame, endFrame, previous, writeMix, null);
            }
            catch (AppendUnavailableException ex)
            {
                // Nothing was modified: fall through and save a separate clip instead.
                Log.Warn($"cannot extend previous clip: {ex.Message}");
                note = "Не удалось дописать в прошлый клип (файл занят?) — сохранено отдельным клипом.";
                _last = null;
            }
        }

        var startFrame = Math.Max(0, endFrame - (long)seconds * AudioFormat.SampleRate);
        if (endFrame - startFrame <= 0) return SaveResult.Fail("Буфер пуст.");

        var saveDirectoryExisted = Directory.Exists(saveDirectory);
        var folder = FileNaming.CreateUniqueFolder(saveDirectory, $"{stamp:yyyy-MM-dd_HH-mm-ss}_replay");
        var result = WriteSegment(folder, startFrame, startFrame, endFrame, previous: null, writeMix, note);
        if (!result.Success)
        {
            // Leave nothing behind when there was nothing to save.
            TryDeleteEmpty(folder);
            if (!saveDirectoryExisted) TryDeleteEmpty(saveDirectory);
        }

        return result;
    }

    /// <summary>The previous clip if this save should extend it, otherwise null.</summary>
    private ClipState? PlanAppend(long endFrame, int seconds, int mergeWindowSeconds)
    {
        if (mergeWindowSeconds <= 0 || _last is not { } last) return null;

        var gap = endFrame - last.EndFrame;
        var limit = (long)Math.Min(mergeWindowSeconds, seconds) * AudioFormat.SampleRate;
        if (gap <= 0 || gap > limit) return null;
        if (!Directory.Exists(last.Folder)) return null;
        if (endFrame - last.StartFrame >= WavFileWriter.MaxFrames - AudioFormat.SampleRate) return null;
        return last;
    }

    /// <summary>
    /// Writes the audio of [segmentStart, endFrame) into <paramref name="folder"/>. Frame positions inside the files
    /// are relative to <paramref name="clipStart"/>. With <paramref name="previous"/> set, the existing files are extended.
    /// </summary>
    private SaveResult WriteSegment(string folder, long clipStart, long segmentStart, long endFrame, ClipState? previous, bool writeMix, string? note)
    {
        var segmentFrames = endFrame - segmentStart;
        if (segmentFrames <= 0) return SaveResult.Fail("Буфер пуст.");

        var firstChunk = segmentStart / AudioFormat.ChunkFrames;
        var lastChunk = (endFrame - 1) / AudioFormat.ChunkFrames;

        var snapshots = new List<(TrackInfo Track, short[]?[] Chunks)>();
        foreach (var ring in _store.SnapshotRings())
        {
            var chunks = ring.SnapshotRange(firstChunk, lastChunk);
            if (HasAudio(chunks, firstChunk, segmentStart, endFrame)) snapshots.Add((ring.Track, chunks));
        }

        if (snapshots.Count == 0)
        {
            return SaveResult.Fail(previous is null ? "В буфере нет звука." : "С прошлого сохранения нового звука не было.");
        }

        snapshots.Sort((a, b) => a.Track.Slot.CompareTo(b.Track.Slot));

        var writers = new Dictionary<string, WavFileWriter>(StringComparer.OrdinalIgnoreCase);
        WavFileWriter? mixWriter = null;
        try
        {
            // 1. Open everything first. If extending fails here, nothing has been modified yet.
            if (previous is not null)
            {
                foreach (var file in previous.TrackFiles)
                {
                    writers[file] = OpenExisting(Path.Combine(folder, file));
                }

                if (previous.HasMix) mixWriter = OpenExisting(Path.Combine(folder, FileNaming.MixFileName));
            }
            else if (writeMix)
            {
                mixWriter = new WavFileWriter(Path.Combine(folder, FileNaming.MixFileName));
            }

            var trackWriters = new WavFileWriter[snapshots.Count];
            for (var i = 0; i < snapshots.Count; i++)
            {
                var file = FileNaming.TrackFileName(snapshots[i].Track);
                if (!writers.TryGetValue(file, out var writer))
                {
                    writer = new WavFileWriter(Path.Combine(folder, file));
                    writers[file] = writer;
                }

                trackWriters[i] = writer;
            }

            // 2. Write the segment.
            var mix = new int[AudioFormat.ChunkSamples];
            var mixOut = new short[AudioFormat.ChunkSamples];
            for (var chunk = firstChunk; chunk <= lastChunk; chunk++)
            {
                var chunkStart = chunk * AudioFormat.ChunkFrames;
                var from = Math.Max(segmentStart, chunkStart);
                var to = Math.Min(endFrame, chunkStart + AudioFormat.ChunkFrames);
                var samples = (int)(to - from) * AudioFormat.Channels;
                var sampleOffset = (int)(from - chunkStart) * AudioFormat.Channels;
                var relativeFrame = from - clipStart;
                var any = false;

                if (mixWriter is not null) Array.Clear(mix, 0, samples);

                for (var i = 0; i < snapshots.Count; i++)
                {
                    if (snapshots[i].Chunks[chunk - firstChunk] is not { } data) continue;

                    var slice = data.AsSpan(sampleOffset, samples);
                    trackWriters[i].WriteAtFrame(relativeFrame, slice);
                    if (mixWriter is not null) AudioMath.MixInto(mix, slice);
                    any = true;
                }

                if (mixWriter is not null && any)
                {
                    AudioMath.ClampTo(mix.AsSpan(0, samples), mixOut.AsSpan(0, samples));
                    mixWriter.WriteAtFrame(relativeFrame, mixOut.AsSpan(0, samples));
                }
            }

            // 3. Every file ends at the same frame: tracks that were silent in this segment are padded.
            var totalFrames = endFrame - clipStart;
            foreach (var writer in writers.Values) writer.Complete(totalFrames);
            mixWriter?.Complete(totalFrames);

            _last = new ClipState(folder, clipStart, endFrame, [.. writers.Keys], mixWriter is not null);

            var total = TimeSpan.FromSeconds(totalFrames / (double)AudioFormat.SampleRate);
            var added = TimeSpan.FromSeconds(segmentFrames / (double)AudioFormat.SampleRate);
            Log.Info($"replay {(previous is null ? "saved" : "extended")}: {folder} ({writers.Count} tracks, +{added.TotalSeconds:0.#}s, total {total.TotalSeconds:0.#}s)");
            return new SaveResult(true, folder, writers.Count, total, note, Appended: previous is not null, Added: added);
        }
        catch (AppendUnavailableException)
        {
            foreach (var writer in writers.Values) writer.Dispose();
            mixWriter?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("saving replay failed", ex);
            foreach (var writer in writers.Values) writer.Dispose();
            mixWriter?.Dispose();
            _last = null;
            return SaveResult.Fail($"Не удалось сохранить: {ex.Message}");
        }
    }

    /// <summary>True if any sample inside [segmentStart, endFrame) is audible (edge chunks may hold sound from outside the range).</summary>
    private static bool HasAudio(short[]?[] chunks, long firstChunk, long segmentStart, long endFrame)
    {
        for (var i = 0; i < chunks.Length; i++)
        {
            if (chunks[i] is not { } data) continue;

            var chunkStart = (firstChunk + i) * AudioFormat.ChunkFrames;
            var from = Math.Max(segmentStart, chunkStart);
            var to = Math.Min(endFrame, chunkStart + AudioFormat.ChunkFrames);
            if (to <= from) continue;

            var slice = data.AsSpan((int)(from - chunkStart) * AudioFormat.Channels, (int)(to - from) * AudioFormat.Channels);
            if (AudioMath.HasSignal(slice)) return true;
        }

        return false;
    }

    private static WavFileWriter OpenExisting(string path)
    {
        try
        {
            return WavFileWriter.OpenForAppend(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new AppendUnavailableException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    private static void TryDeleteEmpty(string folder)
    {
        try { Directory.Delete(folder); } catch { /* not empty, in use or already gone */ }
    }

    private sealed record ClipState(string Folder, long StartFrame, long EndFrame, HashSet<string> TrackFiles, bool HasMix);

    private sealed class AppendUnavailableException(string message, Exception inner) : Exception(message, inner);
}
