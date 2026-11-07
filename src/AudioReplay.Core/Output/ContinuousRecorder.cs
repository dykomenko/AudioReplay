using AudioReplay.Core.Audio;
using AudioReplay.Core.Diagnostics;

namespace AudioReplay.Core.Output;

public sealed record RecorderOptions(string SaveDirectory, bool WriteMix, int MaxPartMinutes);

/// <summary>
/// Streams the live tracks to disk: one WAV per app inside one folder per recording, all with the same start
/// and the same length. It does not capture anything itself - it drains the same <see cref="TrackStore"/> the
/// replay buffer uses, a little behind real time so late packets have landed.
/// </summary>
public sealed class ContinuousRecorder : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private const int SettleMilliseconds = 400;
    private const long SettleFrames = AudioFormat.SampleRate * SettleMilliseconds / 1000;

    private readonly TrackStore _store;
    private readonly Func<RecorderOptions> _options;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly short[] _chunkBuffer = new short[AudioFormat.ChunkSamples];
    private readonly int[] _mixAccumulator = new int[AudioFormat.ChunkSamples];
    private readonly short[] _mixOutput = new short[AudioFormat.ChunkSamples];
    private Thread? _thread;
    private Session? _session;
    private DateTime _stamp;
    private int _part;

    public ContinuousRecorder(TrackStore store, Func<RecorderOptions> options)
    {
        _store = store;
        _options = options;
    }

    public bool IsRecording { get; private set; }

    public string? CurrentFolder
    {
        get
        {
            lock (_gate) return _session?.Folder;
        }
    }

    /// <summary>Raised from the writer thread if disk writing fails; recording has been stopped by then.</summary>
    public event Action<string>? Failed;

    /// <summary>Starts a recording and returns its folder.</summary>
    public string Start()
    {
        lock (_gate)
        {
            if (IsRecording) return _session!.Folder;

            _stamp = DateTime.Now;
            _part = 1;
            _session = OpenSession(FrameClock.ToChunk(FrameClock.NowFrame), _options());
            IsRecording = true;
            _stopSignal.Reset();
            _thread = new Thread(Loop) { IsBackground = true, Name = "recorder" };
            _thread.Start();
            Log.Info($"recording started: {_session.Folder}");
            return _session.Folder;
        }
    }

    public SaveResult Stop()
    {
        lock (_gate)
        {
            if (!IsRecording) return SaveResult.Fail("Запись не идёт.");
        }

        // Let in-flight packets land, then stop the writer thread and flush the tail.
        Thread.Sleep(SettleMilliseconds);
        _stopSignal.Set();
        _thread?.Join();

        lock (_gate)
        {
            IsRecording = false;
            var session = _session;
            _session = null;
            if (session is null) return SaveResult.Fail("Запись не идёт.");

            try
            {
                var nowFrame = FrameClock.NowFrame;
                FlushUpTo(session, (nowFrame + AudioFormat.ChunkFrames - 1) / AudioFormat.ChunkFrames);
                var total = nowFrame - session.StartChunk * AudioFormat.ChunkFrames;
                var summary = CloseSession(session, total);
                Log.Info($"recording stopped: {summary.Folder} ({summary.TrackCount} tracks, {summary.Duration:hh\\:mm\\:ss})");
                return summary;
            }
            catch (Exception ex)
            {
                Log.Error("finishing recording failed", ex);
                return SaveResult.Fail($"Не удалось завершить запись: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
        _stopSignal.Dispose();
    }

    private void Loop()
    {
        while (!_stopSignal.Wait(FlushInterval))
        {
            try
            {
                lock (_gate)
                {
                    if (_session is null) return;
                    var settled = (FrameClock.NowFrame - SettleFrames) / AudioFormat.ChunkFrames;
                    FlushUpTo(_session, settled);
                    RotateIfNeeded();
                }
            }
            catch (Exception ex)
            {
                Log.Error("recording failed", ex);
                Abort(ex.Message);
                return;
            }
        }
    }

    private void RotateIfNeeded()
    {
        var session = _session!;
        var maxFrames = Math.Min((long)session.MaxPartMinutes * 60 * AudioFormat.SampleRate, WavFileWriter.MaxFrames - AudioFormat.SampleRate);
        var written = (session.NextChunk - session.StartChunk) * AudioFormat.ChunkFrames;
        if (written < maxFrames) return;

        CloseSession(session, written);
        _part++;
        _session = OpenSession(session.NextChunk, _options());
        Log.Info($"recording part {_part}: {_session.Folder}");
    }

    private void Abort(string reason)
    {
        lock (_gate)
        {
            IsRecording = false;
            var session = _session;
            _session = null;
            if (session is not null)
            {
                foreach (var writer in session.AllWriters()) writer.Dispose();
            }
        }

        Failed?.Invoke(reason);
    }

    private Session OpenSession(long startChunk, RecorderOptions options)
    {
        var suffix = _part > 1 ? $"_part{_part}" : string.Empty;
        var folder = FileNaming.CreateUniqueFolder(options.SaveDirectory, $"{_stamp:yyyy-MM-dd_HH-mm-ss}_rec{suffix}");
        return new Session(folder, startChunk, options);
    }

    /// <summary>Writes every chunk in [session.NextChunk, endChunkExclusive).</summary>
    private void FlushUpTo(Session session, long endChunkExclusive)
    {
        if (endChunkExclusive <= session.NextChunk) return;

        var rings = _store.SnapshotRings();
        for (var chunk = session.NextChunk; chunk < endChunkExclusive; chunk++)
        {
            var any = false;
            var frameIndex = (chunk - session.StartChunk) * AudioFormat.ChunkFrames;
            if (session.Options.WriteMix) Array.Clear(_mixAccumulator);

            foreach (var ring in rings)
            {
                if (!ring.TryCopyChunk(chunk, _chunkBuffer)) continue;

                if (!session.Writers.TryGetValue(ring.Track.Id, out var writer))
                {
                    writer = new WavFileWriter(Path.Combine(session.Folder, FileNaming.TrackFileName(ring.Track)));
                    session.Writers[ring.Track.Id] = writer;
                }

                writer.WriteAtFrame(frameIndex, _chunkBuffer);
                if (session.Options.WriteMix) AudioMath.MixInto(_mixAccumulator, _chunkBuffer);
                any = true;
            }

            if (session.Options.WriteMix && any)
            {
                session.Mix ??= new WavFileWriter(Path.Combine(session.Folder, FileNaming.MixFileName));
                AudioMath.ClampTo(_mixAccumulator, _mixOutput);
                session.Mix.WriteAtFrame(frameIndex, _mixOutput);
            }
        }

        session.NextChunk = endChunkExclusive;
    }

    private static SaveResult CloseSession(Session session, long totalFrames)
    {
        var tracks = session.Writers.Count;
        foreach (var writer in session.AllWriters()) writer.Complete(totalFrames);

        if (tracks == 0)
        {
            // Nothing was audible: do not leave an empty folder behind.
            try { Directory.Delete(session.Folder); } catch { /* not empty or in use */ }
            return new SaveResult(false, null, 0, TimeSpan.FromSeconds(totalFrames / (double)AudioFormat.SampleRate), "Во время записи не было звука.");
        }

        return new SaveResult(true, session.Folder, tracks, TimeSpan.FromSeconds(totalFrames / (double)AudioFormat.SampleRate));
    }

    private sealed class Session(string folder, long startChunk, RecorderOptions options)
    {
        public string Folder { get; } = folder;

        public long StartChunk { get; } = startChunk;

        public long NextChunk { get; set; } = startChunk;

        public RecorderOptions Options { get; } = options;

        public int MaxPartMinutes => Options.MaxPartMinutes;

        public Dictionary<string, WavFileWriter> Writers { get; } = new(StringComparer.Ordinal);

        public WavFileWriter? Mix { get; set; }

        public IEnumerable<WavFileWriter> AllWriters() => Mix is null ? Writers.Values : Writers.Values.Append(Mix);
    }
}
