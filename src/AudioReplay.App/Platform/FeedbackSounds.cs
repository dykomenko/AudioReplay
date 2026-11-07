using System.Media;
using AudioReplay.Core.Audio;
using AudioReplay.Core.Diagnostics;

namespace AudioReplay.App.Platform;

internal enum FeedbackKind
{
    /// <summary>A replay clip was saved.</summary>
    Saved,
    RecordingStarted,
    RecordingStopped,

    /// <summary>Soft failure, e.g. nothing in the buffer.</summary>
    Warning,

    /// <summary>Something broke (disk, hotkey, device).</summary>
    Error,
}

/// <summary>Short generated beeps used as feedback when a pop-up is not wanted (streamer mode) or not enough.</summary>
internal sealed class FeedbackSounds : IDisposable
{
    private readonly Dictionary<FeedbackKind, SoundPlayer> _players = new()
    {
        [FeedbackKind.Saved] = Create((880, 70), (1175, 100)),
        [FeedbackKind.RecordingStarted] = Create((660, 80), (990, 110)),
        [FeedbackKind.RecordingStopped] = Create((990, 80), (660, 110)),
        [FeedbackKind.Warning] = Create((440, 150)),
        [FeedbackKind.Error] = Create((330, 120), (247, 200)),
    };

    public void Play(FeedbackKind kind)
    {
        try
        {
            _players[kind].Play(); // asynchronous, returns immediately
        }
        catch (Exception ex)
        {
            Log.Debug($"cannot play feedback sound: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var player in _players.Values) player.Dispose();
    }

    private static SoundPlayer Create(params (double Hz, int Milliseconds)[] tones)
    {
        var player = new SoundPlayer(new MemoryStream(ToneWav.Create(tones)));
        player.Load();
        return player;
    }
}
