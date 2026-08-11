using System.Text;

namespace Engram.Core;

/// <summary>
/// Sessions the <c>memory-guard</c> hook has already nudged once, one <c>session_id</c> per line
/// at <see cref="EngramHome.MemoryGuardStatePath"/>.
/// </summary>
/// <remarks>
/// No compaction and no pruning: one line per session that ever touched a file-based memory file
/// is bounded by real session count. A rewrite-style compactor here could lose a line to a race
/// with a concurrent append, and the cost of that would only ever be one extra nudge — not worth
/// building against in v1.
/// </remarks>
public static class MemoryGuardState
{
    private static readonly TimeSpan AppendRetryBudget = TimeSpan.FromMilliseconds(500);

    public static bool Contains(EngramHome home, string sessionId)
    {
        if (!File.Exists(home.MemoryGuardStatePath))
        {
            return false;
        }

        try
        {
            return File.ReadLines(home.MemoryGuardStatePath)
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
    public static bool TryAppend(EngramHome home, string sessionId)
    {
        var payload = Encoding.UTF8.GetBytes(sessionId + "\n");
        DurableAppend.TryAppend(home.MemoryGuardStatePath, payload, AppendRetryBudget);
        return Contains(home, sessionId);
    }
}
