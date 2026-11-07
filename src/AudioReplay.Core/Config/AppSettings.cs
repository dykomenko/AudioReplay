using System.Text.Json.Serialization;
using AudioReplay.Core.Input;

namespace AudioReplay.Core.Config;

/// <summary>User-editable settings, persisted as JSON in %APPDATA%\AudioReplay\settings.json.</summary>
public sealed class AppSettings
{
    public const int MinReplaySeconds = 5;
    public const int MaxReplaySeconds = 900;

    public string SaveDirectory { get; set; } = AppPaths.DefaultSaveDirectory;

    /// <summary>Keep the rolling in-memory buffer ("save the last N seconds").</summary>
    public bool ReplayBufferEnabled { get; set; } = true;

    /// <summary>Length of the rolling buffer and of the clip written by the replay hotkey.</summary>
    public int ReplaySeconds { get; set; } = 120;

    public string ReplayHotkey { get; set; } = "Alt+Q";

    public string RecordHotkey { get; set; } = "Ctrl+Alt+Shift+Q";

    /// <summary>Capture a microphone as one more track. Off by default (privacy).</summary>
    public bool CaptureMicrophone { get; set; }

    /// <summary>MMDevice id of the microphone; empty = Windows default capture device.</summary>
    public string MicrophoneDeviceId { get; set; } = string.Empty;

    /// <summary>Also write a "mix.wav" with all tracks summed.</summary>
    public bool WriteMixTrack { get; set; } = true;

    /// <summary>Continuous recording is split into parts of at most this length (WAV is limited to 4 GB).</summary>
    public int MaxRecordingMinutes { get; set; } = 180;

    public bool ShowTrayIcon { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    public bool StartWithWindows { get; set; }

    /// <summary>Applications that are never captured (exe names, with or without ".exe").</summary>
    public List<string> ExcludedApps { get; set; } = [];

    [JsonIgnore]
    public Hotkey ReplayHotkeyValue => Hotkey.TryParse(ReplayHotkey, out var h) ? h : default;

    [JsonIgnore]
    public Hotkey RecordHotkeyValue => Hotkey.TryParse(RecordHotkey, out var h) ? h : default;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ExcludedApps = [.. ExcludedApps];
        return copy;
    }

    /// <summary>Clamps values into valid ranges and replaces unusable ones with defaults.</summary>
    public void Normalize()
    {
        var defaults = new AppSettings();

        if (string.IsNullOrWhiteSpace(SaveDirectory)) SaveDirectory = defaults.SaveDirectory;
        ReplaySeconds = Math.Clamp(ReplaySeconds, MinReplaySeconds, MaxReplaySeconds);
        MaxRecordingMinutes = Math.Clamp(MaxRecordingMinutes, 5, 240);

        if (!Hotkey.TryParse(ReplayHotkey, out _)) ReplayHotkey = defaults.ReplayHotkey;
        if (!Hotkey.TryParse(RecordHotkey, out _)) RecordHotkey = defaults.RecordHotkey;

        MicrophoneDeviceId ??= string.Empty;
        ExcludedApps = ExcludedApps
            .Select(AppNames.Normalize)
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
    }
}
