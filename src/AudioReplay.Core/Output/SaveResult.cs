namespace AudioReplay.Core.Output;

/// <summary>Outcome of saving a replay clip or finishing a recording.</summary>
public sealed record SaveResult(bool Success, string? Folder, int TrackCount, TimeSpan Duration, string? Message = null)
{
    public static SaveResult Fail(string message) => new(false, null, 0, TimeSpan.Zero, message);
}
