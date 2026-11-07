using System.Diagnostics;
using AudioReplay.App.Platform;
using AudioReplay.App.UI;
using AudioReplay.Core;
using AudioReplay.Core.Audio;
using AudioReplay.Core.Capture;
using AudioReplay.Core.Config;
using AudioReplay.Core.Diagnostics;
using Microsoft.Win32;

namespace AudioReplay.App;

/// <summary>Composition root: wires engine, tray icon, hotkeys and the control pipe together.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int ReplayHotkeyId = 1;
    private const int RecordHotkeyId = 2;

    private readonly Control _ui = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _saveItem = new();
    private readonly ToolStripMenuItem _recordItem = new();
    private readonly HotkeyManager _hotkeys;
    private readonly ControlPipe _pipe;
    private readonly AudioHub _hub;
    private readonly FeedbackSounds _sounds = new();
    private readonly System.Windows.Forms.Timer _hideTrayTimer = new() { Interval = 8000 };
    private readonly Icon _idleIcon = TrayIcons.Create(recording: false);
    private readonly Icon _recordingIcon = TrayIcons.Create(recording: true);
    private AppSettings _settings;
    private SettingsForm? _settingsForm;
    private string? _lastFolder;
    private bool _busy;

    public TrayApplicationContext()
    {
        Log.Init(AppPaths.LogFile);
        Log.Info($"AudioReplay {typeof(TrayApplicationContext).Assembly.GetName().Version} starting");

        _ui.CreateControl();
        _ = _ui.Handle; // forces handle creation so BeginInvoke works from any thread

        var firstRun = !File.Exists(AppPaths.SettingsFile);
        _settings = SettingsStore.Load(AppPaths.SettingsFile);
        if (firstRun) SettingsStore.Save(_settings, AppPaths.SettingsFile);

        _hub = new AudioHub(_settings, new TrackRegistry(AppPaths.TracksFile));
        _hub.Error += message => OnUi(() => Feedback(FeedbackKind.Error, message));
        _hub.RecordingChanged += _ => OnUi(RefreshRecordingState);

        _hotkeys = new HotkeyManager();

        _saveItem.Click += (_, _) => SaveReplay();
        _recordItem.Click += (_, _) => ToggleRecording();
        var menu = new ContextMenuStrip();
        menu.Items.Add(_saveItem);
        menu.Items.Add(_recordItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть папку с записями", null, (_, _) => OpenFolder(_settings.SaveDirectory));
        menu.Items.Add("Настройки…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "AudioReplay",
            ContextMenuStrip = menu,
            Visible = _settings.ShowTrayIcon,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => OpenFolder(_lastFolder ?? _settings.SaveDirectory);

        _hideTrayTimer.Tick += (_, _) =>
        {
            _hideTrayTimer.Stop();
            if (!_settings.ShowTrayIcon) _tray.Visible = false;
        };

        _pipe = new ControlPipe(command => OnUi(() => HandleCommand(command)));
        SystemEvents.SessionEnding += OnSessionEnding;

        RegisterHotkeys();
        UpdateMenuText();
        Autostart.Apply(_settings.StartWithWindows);
        EnsureSaveDirectory();
        _hub.Start();
    }

    private void HandleCommand(string command)
    {
        switch (command)
        {
            case ControlPipe.SaveCommand: SaveReplay(); break;
            case ControlPipe.RecordCommand: ToggleRecording(); break;
            case ControlPipe.ExitCommand: Quit(); break;
            default: OpenSettings(); break;
        }
    }

    private async void SaveReplay()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var result = await _hub.SaveReplayAsync();
            if (result.Success)
            {
                _lastFolder = result.Folder;
                Feedback(FeedbackKind.Saved, $"Откат сохранён: {result.Duration.TotalSeconds:0} с, дорожек: {result.TrackCount}");
            }
            else
            {
                Feedback(FeedbackKind.Warning, result.Message ?? "Не удалось сохранить откат.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("save replay failed", ex);
            Feedback(FeedbackKind.Error, $"Ошибка: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void ToggleRecording()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_hub.IsRecording)
            {
                var result = await _hub.StopRecordingAsync();
                if (result.Success)
                {
                    _lastFolder = result.Folder;
                    Feedback(FeedbackKind.RecordingStopped, $"Запись сохранена: {result.Duration:hh\\:mm\\:ss}, дорожек: {result.TrackCount}");
                }
                else
                {
                    Feedback(FeedbackKind.Warning, result.Message ?? "Запись остановлена.");
                }
            }
            else
            {
                var folder = await _hub.StartRecordingAsync();
                if (folder is not null)
                {
                    _lastFolder = folder;
                    Feedback(FeedbackKind.RecordingStarted, "Запись началась");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("toggle recording failed", ex);
            Feedback(FeedbackKind.Error, $"Ошибка: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RefreshRecordingState();
        }
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        List<(string Id, string Name)> microphones;
        try
        {
            microphones = AudioHub.ListMicrophones();
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot list microphones: {ex.Message}");
            microphones = [];
        }

        // Release our hotkeys so the combination being recorded is not swallowed by the app itself.
        _hotkeys.Suspend();
        using var form = new SettingsForm(_settings, microphones);
        _settingsForm = form;
        try
        {
            if (form.ShowDialog() == DialogResult.OK) ApplySettings(form.Result);
        }
        finally
        {
            _settingsForm = null;
            _hotkeys.Resume();
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        try
        {
            SettingsStore.Save(settings, AppPaths.SettingsFile);
        }
        catch (Exception ex)
        {
            Log.Error("cannot save settings", ex);
            Feedback(FeedbackKind.Error, $"Не удалось сохранить настройки: {ex.Message}");
        }

        _settings = settings;
        _hub.ApplySettings(settings);
        Autostart.Apply(settings.StartWithWindows);
        _tray.Visible = settings.ShowTrayIcon || _hideTrayTimer.Enabled;
        RegisterHotkeys();
        UpdateMenuText();
        EnsureSaveDirectory();
    }

    private void RegisterHotkeys()
    {
        var replay = true;
        if (_settings.ReplayBufferEnabled) replay = _hotkeys.Register(ReplayHotkeyId, _settings.ReplayHotkeyValue, SaveReplay);
        else _hotkeys.Unregister(ReplayHotkeyId);
        var record = _hotkeys.Register(RecordHotkeyId, _settings.RecordHotkeyValue, ToggleRecording);

        if (!replay) Feedback(FeedbackKind.Error, $"Хоткей {_settings.ReplayHotkey} занят другим приложением — выберите другой в настройках.");
        if (!record) Feedback(FeedbackKind.Error, $"Хоткей {_settings.RecordHotkey} занят другим приложением — выберите другой в настройках.");
    }

    private void UpdateMenuText()
    {
        // The hotkey goes into the right-aligned shortcut column; a tab inside Text would run into the label.
        _saveItem.Text = $"Сохранить последние {_settings.ReplaySeconds} с";
        _saveItem.ShortcutKeyDisplayString = _settings.ReplayHotkey;
        _saveItem.Enabled = _settings.ReplayBufferEnabled;
        RefreshRecordingState();
    }

    private void RefreshRecordingState()
    {
        var recording = _hub.IsRecording;
        _recordItem.Text = recording ? "Остановить запись" : "Начать запись";
        _recordItem.ShortcutKeyDisplayString = _settings.RecordHotkey;
        _tray.Icon = recording ? _recordingIcon : _idleIcon;
        var status = recording ? "● идёт запись" : _settings.ReplayBufferEnabled ? $"буфер {_settings.ReplaySeconds} с" : "пауза";
        _tray.Text = $"AudioReplay — {status}";
    }

    /// <summary>
    /// The one place that talks to the user. Sound: whenever enabled. Pop-up: never in streamer mode while a
    /// streaming app runs; otherwise when notifications are on (errors are shown even if they are off).
    /// </summary>
    private void Feedback(FeedbackKind kind, string text)
    {
        Log.Info($"feedback [{kind}]: {text}");

        if (_settings.PlaySound) _sounds.Play(kind);

        if (_settings.StreamerMode && StreamingAppDetector.IsAnyRunning(_settings.StreamerApps))
        {
            Log.Info("pop-up suppressed: streamer mode (streaming app is running)");
            return;
        }

        if (kind != FeedbackKind.Error && !_settings.ShowNotifications) return;

        ShowBalloon(text);
    }

    /// <summary>If the tray icon is hidden it appears only for the duration of the notice.</summary>
    private void ShowBalloon(string text)
    {
        if (!_tray.Visible) _tray.Visible = true;

        _hideTrayTimer.Stop();
        _hideTrayTimer.Start();
        _tray.ShowBalloonTip(3000, "AudioReplay", text, ToolTipIcon.None);
    }

    private void EnsureSaveDirectory()
    {
        try
        {
            Directory.CreateDirectory(_settings.SaveDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot create save directory: {ex.Message}");
            Feedback(FeedbackKind.Error, $"Папка для записей недоступна: {ex.Message}");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot open folder: {ex.Message}");
        }
    }

    private void OnUi(Action action)
    {
        if (_ui.IsDisposed) return;
        try
        {
            _ui.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e) => Quit();

    private void Quit()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        _hideTrayTimer.Stop();
        _tray.Visible = false;
        _pipe.Dispose();
        _hotkeys.Dispose();
        _hub.Dispose(); // finishes a running recording so the WAV headers are valid
        _sounds.Dispose();
        _tray.Dispose();
        Log.Info("AudioReplay exiting");
        ExitThread();
    }
}
