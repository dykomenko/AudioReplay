using AudioReplay.Core.Audio;
using AudioReplay.Core.Capture;
using AudioReplay.Core.Config;
using AudioReplay.Core.Diagnostics;
using AudioReplay.Core.Output;

namespace AudioReplay.Core;

/// <summary>
/// Facade over the whole engine: capture -> track store -> (replay saver | continuous recorder).
/// All COM work is done on thread-pool (MTA) threads, never on the caller's UI thread.
/// </summary>
public sealed class AudioHub : IDisposable
{
    /// <summary>Retention used when the replay buffer is off but a recording is running.</summary>
    private static readonly TimeSpan RecordingOnlyRetention = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly TrackRegistry _registry;
    private readonly TrackStore _store;
    private readonly ReplaySaver _saver;
    private readonly ContinuousRecorder _recorder;
    private readonly Timer _maintenance;
    private AppSettings _settings;
    private volatile IReadOnlySet<string> _excluded = new HashSet<string>();
    private SessionWatcher? _watcher;
    private CaptureWorker? _mic;
    private string _micDeviceId = string.Empty;
    private DateTime _micRetryAt = DateTime.MinValue;
    private bool _disposed;

    public AudioHub(AppSettings settings, TrackRegistry registry)
    {
        _registry = registry;
        _settings = settings.Clone();
        _store = new TrackStore(RetentionFor(_settings));
        _saver = new ReplaySaver(_store);
        _recorder = new ContinuousRecorder(_store, () =>
        {
            lock (_gate) return new RecorderOptions(_settings.SaveDirectory, _settings.WriteMixTrack, _settings.MaxRecordingMinutes);
        });
        _recorder.Failed += reason =>
        {
            Error?.Invoke($"Запись остановлена: {reason}");
            RecordingChanged?.Invoke(false);
            RunMta(Reconcile);
        };

        UpdateExcluded();
        _maintenance = new Timer(_ => Maintain(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public bool IsRecording => _recorder.IsRecording;

    public string? RecordingFolder => _recorder.CurrentFolder;

    public event Action<string>? Error;

    public event Action<bool>? RecordingChanged;

    public static List<(string Id, string Name)> ListMicrophones() => RunMta(MicrophoneReader.ListDevices);

    /// <summary>Starts the engine according to the current settings.</summary>
    public void Start() => RunMta(Reconcile);

    public void ApplySettings(AppSettings settings)
    {
        lock (_gate)
        {
            _settings = settings.Clone();
            _store.SetRetention(RetentionFor(_settings));
            UpdateExcluded();
        }

        RunMta(Reconcile);
    }

    public async Task<SaveResult> SaveReplayAsync()
    {
        AppSettings s;
        lock (_gate) s = _settings;

        if (!s.ReplayBufferEnabled) return SaveResult.Fail("Буфер отката выключен в настройках.");
        return await _saver.SaveAsync(s.ReplaySeconds, s.SaveDirectory, s.WriteMixTrack).ConfigureAwait(false);
    }

    public async Task<string?> StartRecordingAsync()
    {
        return await Task.Run(() =>
        {
            string folder;
            try
            {
                folder = _recorder.Start();
            }
            catch (Exception ex)
            {
                Log.Error("cannot start recording", ex);
                Error?.Invoke($"Не удалось начать запись: {ex.Message}");
                return null;
            }

            Reconcile();
            RecordingChanged?.Invoke(true);
            return folder;
        }).ConfigureAwait(false);
    }

    public async Task<SaveResult> StopRecordingAsync()
    {
        return await Task.Run(() =>
        {
            var result = _recorder.Stop();
            Reconcile();
            RecordingChanged?.Invoke(false);
            return result;
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _maintenance.Dispose();
        if (_recorder.IsRecording) _recorder.Stop();
        _recorder.Dispose();
        RunMta(() =>
        {
            lock (_gate)
            {
                StopMic();
                _watcher?.Dispose();
                _watcher = null;
            }
        });
    }

    private static TimeSpan RetentionFor(AppSettings s) =>
        s.ReplayBufferEnabled ? TimeSpan.FromSeconds(s.ReplaySeconds) : RecordingOnlyRetention;

    private void UpdateExcluded() => _excluded = new HashSet<string>(_settings.ExcludedApps, StringComparer.Ordinal);

    /// <summary>Brings watcher and microphone in line with "buffer enabled || recording".</summary>
    private void Reconcile()
    {
        lock (_gate)
        {
            if (_disposed) return;

            var needed = _settings.ReplayBufferEnabled || _recorder.IsRecording;

            if (needed && _watcher is null)
            {
                _watcher = new SessionWatcher(_store, _registry, () => _excluded);
                _watcher.Start();
                Log.Info("engine started");
            }
            else if (!needed && _watcher is not null)
            {
                _watcher.Dispose();
                _watcher = null;
                _store.Clear();
                Log.Info("engine stopped");
            }

            var wantMic = needed && _settings.CaptureMicrophone;
            if (wantMic)
            {
                if (_mic is null || _mic.Faulted || _micDeviceId != _settings.MicrophoneDeviceId) StartMic();
            }
            else
            {
                StopMic();
            }
        }
    }

    private void StartMic()
    {
        StopMic();
        if (DateTime.UtcNow < _micRetryAt) return;

        try
        {
            _micDeviceId = _settings.MicrophoneDeviceId;
            var reader = new MicrophoneReader(_micDeviceId);
            _mic = new CaptureWorker("microphone", _registry.Get(TrackRegistry.MicrophoneId), reader, _store);
            _mic.Start();
            Log.Info("microphone capture started");
        }
        catch (Exception ex)
        {
            _micRetryAt = DateTime.UtcNow.AddSeconds(10);
            Log.Warn($"cannot open microphone: {ex.Message}");
        }
    }

    private void StopMic()
    {
        _mic?.Dispose();
        _mic = null;
    }

    private void Maintain()
    {
        try
        {
            _store.Trim(FrameClock.NowFrame);

            lock (_gate)
            {
                // Recover from an unplugged/replugged microphone.
                if (!_disposed && _settings.CaptureMicrophone && _watcher is not null && (_mic is null || _mic.Faulted))
                    StartMic(); // the timer callback already runs on a pool (MTA) thread
            }
        }
        catch (Exception ex)
        {
            Log.Error("maintenance failed", ex);
        }
    }

    private static void RunMta(Action action) => Task.Run(action).GetAwaiter().GetResult();

    private static T RunMta<T>(Func<T> func) => Task.Run(func).GetAwaiter().GetResult();
}
