using AudioReplay.Core.Config;

namespace AudioReplay.Core.Capture;

internal sealed record SessionInfo(uint Pid, bool Active);

internal sealed record CaptureTarget(uint Pid, string TrackId);

/// <param name="Targets">Processes that should be captured, one capture per process.</param>
/// <param name="Skipped">Active PIDs that must NOT be captured (excluded app, or covered by an ancestor's capture).</param>
internal sealed record CapturePlan(IReadOnlyList<CaptureTarget> Targets, IReadOnlySet<uint> Skipped);

/// <summary>
/// Decides which processes to capture. Pure function, unit-tested.
/// A process-loopback capture of PID X also contains X's child processes, so if an application's process
/// has an ancestor with the same executable that is also playing, capturing both would double the audio:
/// only the topmost one is captured.
/// </summary>
internal static class CapturePlanner
{
    private const int MaxDepth = 32;

    public static string TrackIdOf(ProcInfo process) => AppNames.Normalize(process.ExeName);

    public static CapturePlan Plan(
        IEnumerable<SessionInfo> sessions,
        IReadOnlyDictionary<uint, ProcInfo> processes,
        Func<string, bool> isExcluded,
        uint selfPid)
    {
        var skipped = new HashSet<uint>();
        var byTrack = new Dictionary<string, List<uint>>(StringComparer.Ordinal);

        foreach (var pid in sessions.Where(s => s.Active).Select(s => s.Pid).Distinct())
        {
            if (pid == 0 || pid == selfPid || !processes.TryGetValue(pid, out var proc)) continue;

            var trackId = TrackIdOf(proc);
            if (trackId.Length == 0) continue;

            if (isExcluded(trackId))
            {
                skipped.Add(pid);
                continue;
            }

            if (!byTrack.TryGetValue(trackId, out var list)) byTrack[trackId] = list = [];
            list.Add(pid);
        }

        var targets = new List<CaptureTarget>();
        foreach (var (trackId, pids) in byTrack)
        {
            var set = pids.ToHashSet();
            foreach (var pid in pids)
            {
                if (HasAncestorIn(pid, set, processes)) skipped.Add(pid);
                else targets.Add(new CaptureTarget(pid, trackId));
            }
        }

        return new CapturePlan(targets, skipped);
    }

    private static bool HasAncestorIn(uint pid, HashSet<uint> candidates, IReadOnlyDictionary<uint, ProcInfo> processes)
    {
        var current = pid;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (!processes.TryGetValue(current, out var proc)) return false;
            var parent = proc.ParentPid;
            // parent == pid means the chain looped back (PID reuse): a process is never its own ancestor.
            if (parent == 0 || parent == current || parent == pid) return false;
            if (candidates.Contains(parent)) return true;
            current = parent;
        }

        return false;
    }
}
