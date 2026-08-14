namespace Engram.Core;

/// <summary>
/// Records that indexing is under way, so something watching the event stream can say so.
/// </summary>
/// <remarks>
/// Moved out of the CLI (it began as <c>IndexCommand.Note</c>) because the background freshness
/// service runs inside the MCP server, not the CLI, and needs the same emitter. <c>sessionId</c>
/// is a parameter rather than a hardcoded constant for the same reason: the CLI passes
/// <c>"cli"</c>, as it always has, and the server passes <c>"server"</c> — a third honest value in
/// the id space D43 already established as disjoint, never a borrowed one. A finished phase is
/// what lets a reader stop saying "indexing"; without the pair, the only alternative is a timer,
/// and a timer is a guess about how long a repo takes. <paramref name="repo"/> is resolved once by
/// the caller so every phase of one invocation carries the same identity, computed the same way
/// regardless of whether the run ever reaches a registered <c>repo_path</c>.
/// </remarks>
public static class IndexTelemetry
{
    public static void Note(EngramHome home, string sessionId, string phase, string repo)
    {
        if (File.Exists(home.ConfigPath))
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.Index,
                Phase: phase,
                Repo: repo));
        }
    }
}
