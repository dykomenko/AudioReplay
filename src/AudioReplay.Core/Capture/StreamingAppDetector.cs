using AudioReplay.Core.Config;

namespace AudioReplay.Core.Capture;

/// <summary>
/// Tells whether a streaming/recording program such as OBS is running. Uses the handle-free process snapshot,
/// so it never opens the processes it looks at.
/// </summary>
public static class StreamingAppDetector
{
    public static readonly IReadOnlyList<string> DefaultApps =
        ["obs64", "obs32", "obs", "streamlabs desktop", "streamlabs obs"];

    /// <param name="appNames">Normalised exe names (see <see cref="AppNames.Normalize"/>).</param>
    public static bool IsAnyRunning(IEnumerable<string> appNames) =>
        IsAnyRunning(ProcessSnapshot.Take().Values, appNames.ToHashSet(StringComparer.Ordinal));

    internal static bool IsAnyRunning(IEnumerable<ProcInfo> processes, IReadOnlySet<string> appNames) =>
        processes.Any(p => appNames.Contains(AppNames.Normalize(p.ExeName)));
}
