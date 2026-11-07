using AudioReplay.Core.Audio;
using AudioReplay.Core.Diagnostics;

namespace AudioReplay.Core.Capture;

/// <summary>
/// Watches which applications are currently playing audio and keeps one capture per playing process.
/// Everything it learns comes from the audio session API and a handle-free process snapshot.
/// </summary>
internal sealed class SessionWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly TrackStore _store;
    private readonly TrackRegistry _registry;
    private readonly Func<IReadOnlySet<string>> _excluded;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Dictionary<uint, Running> _running = [];
    private readonly Dictionary<uint, DateTime> _retryAfter = [];
    private readonly RenderSessionEnumerator _sessions = new();
    private Thread? _thread;

    public SessionWatcher(TrackStore store, TrackRegistry registry, Func<IReadOnlySet<string>> excludedApps)
    {
        _store = store;
        _registry = registry;
        _excluded = excludedApps;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "session-watcher" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
        foreach (var running in _running.Values) running.Worker.Dispose();
        _running.Clear();
        _sessions.Dispose();
    }

    private void Run()
    {
        while (!_stop.Wait(PollInterval))
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Error("session watcher tick failed", ex);
            }
        }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var processes = ProcessSnapshot.Take();
        var sessions = _sessions.Enumerate();
        var excluded = _excluded();
        // AudioReplay itself (any instance) is never captured: its own capture clients show up as sessions.
        var plan = CapturePlanner.Plan(sessions, processes, id => id == "audioreplay" || excluded.Contains(id), (uint)Environment.ProcessId);

        foreach (var target in plan.Targets)
        {
            if (_running.TryGetValue(target.Pid, out var existing))
            {
                existing.LastWanted = now;
                continue;
            }

            if (_retryAfter.TryGetValue(target.Pid, out var retryAt) && now < retryAt) continue;

            StartCapture(target, now);
        }

        foreach (var (pid, running) in _running.ToArray())
        {
            var gone = !processes.ContainsKey(pid);
            var duplicate = plan.Skipped.Contains(pid);
            var idle = now - running.LastWanted > IdleTimeout;

            if (running.Worker.Faulted || gone || duplicate || idle)
            {
                Log.Info($"capture stopped: {running.TrackId} (pid {pid}) - {(gone ? "process exited" : duplicate ? "covered elsewhere" : idle ? "idle" : "stream error")}");
                running.Worker.Dispose();
                _running.Remove(pid);
            }
        }

        foreach (var pid in _retryAfter.Where(kv => now >= kv.Value).Select(kv => kv.Key).ToArray())
            _retryAfter.Remove(pid);
    }

    private void StartCapture(CaptureTarget target, DateTime now)
    {
        try
        {
            var reader = ProcessLoopbackReader.Create(target.Pid);
            var worker = new CaptureWorker($"{target.TrackId}:{target.Pid}", _registry.Get(target.TrackId), reader, _store);
            worker.Start();
            _running[target.Pid] = new Running(worker, target.TrackId, now);
            Log.Info($"capture started: {target.TrackId} (pid {target.Pid})");
        }
        catch (Exception ex)
        {
            _retryAfter[target.Pid] = now + RetryDelay;
            Log.Warn($"cannot capture {target.TrackId} (pid {target.Pid}): {ex.Message}");
        }
    }

    private sealed class Running(CaptureWorker worker, string trackId, DateTime lastWanted)
    {
        public CaptureWorker Worker { get; } = worker;

        public string TrackId { get; } = trackId;

        public DateTime LastWanted { get; set; } = lastWanted;
    }
}
