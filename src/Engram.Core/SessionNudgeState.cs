using System.Text;

namespace Engram.Core;

/// <summary>
/// Sessions a once-per-session PreToolUse nudge has already fired for, one <c>session_id</c> per
/// line at a caller-supplied path. Shared by <c>memory-guard</c>
/// (<see cref="EngramHome.MemoryGuardStatePath"/>) and <c>lookup-nudge</c>
/// (<see cref="EngramHome.LookupNudgeStatePath"/>), which differ only in which file they count in.
/// </summary>
/// <remarks>
/// No compaction and no pruning: one line per session that ever tripped a nudge is bounded by
/// real session count. A rewrite-style compactor here could lose a line to a race with a
/// concurrent append, and the cost of that would only ever be one extra nudge.
/// </remarks>
public static class SessionNudgeState
{
    private static readonly TimeSpan AppendRetryBudget = TimeSpan.FromMilliseconds(500);

    public static bool Contains(string statePath, string sessionId)
    {
        if (!File.Exists(statePath))
        {
            return false;
        }

        try
        {
            return File.ReadLines(statePath)
                .Any(line => string.Equals(line, sessionId, StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records the session as nudged. Returns whether the write actually landed —
    /// <see cref="DurableAppend.TryAppend"/> is best-effort and returns void either way, so the
    /// only way to confirm success is reading the line back.
    /// </summary>
    public static bool TryAppend(string statePath, string sessionId)
    {
        var payload = Encoding.UTF8.GetBytes(sessionId + "\n");
        DurableAppend.TryAppend(statePath, payload, AppendRetryBudget);
        return Contains(statePath, sessionId);
    }
}
