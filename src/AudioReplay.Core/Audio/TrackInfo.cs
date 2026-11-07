namespace AudioReplay.Core.Audio;

/// <summary>
/// Identity of one track. <see cref="Id"/> is the normalised executable name ("chrome") or "microphone";
/// <see cref="Slot"/> is a permanent number assigned by <see cref="TrackRegistry"/>, so the same app always
/// gets the same position/file prefix across recordings.
/// </summary>
public sealed record TrackInfo(string Id, int Slot);
