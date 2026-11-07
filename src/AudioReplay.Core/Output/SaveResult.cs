namespace AudioReplay.Core.Output;

/// <summary>Outcome of saving a replay clip or finishing a recording.</summary>
/// <param name="Duration">Total length of the clip/recording on disk.</param>
/// <param name="Message">Failure reason, or a note on a successful save (e.g. why a clip could not be extended).</param>
/// <param name="Appended">True if the audio was added to the previous clip instead of creating a new one.</param>
/// <param name="Added">Length of the part that was just saved (equals <paramref name="Duration"/> for a new clip).</param>
public sealed record SaveResult(
    bool Success,
    string? Folder,
    int TrackCount,
    TimeSpan Duration,
    string? Message = null,
    bool Appended = false,
    TimeSpan Added = default)
{
    public static SaveResult Fail(string message) => new(false, null, 0, TimeSpan.Zero, message);
}
