using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>A session note as recall needs it.</summary>
/// <remarks>
/// <paramref name="SessionId"/> is the <c>session</c> row id, not the host's session string.
/// Recall only ever compares it and groups by it, and the row id is what the path carries, so
/// resolving the external id would be a join taken for nothing.
/// </remarks>
public sealed record SessionFact(
    long FactId,
    long SessionId,
    string Statement,
    string? Subject,
    string? Agent,
    int AgeDays);

/// <summary>
/// Working memory: what the model would otherwise hold in context and lose to compaction,
/// to a subagent's lossy report, or to a subagent that dies (D11).
/// </summary>
/// <remarks>
/// These were one JSONL file per session, which made a session note the one kind of memory
/// that could not be retracted — there was no notion of a closed record in that format at
/// all. Being facts, they are now closed by the same <see cref="FactStore.Forget"/> as
/// everything else.
///
/// <para>
/// <c>/sessions/&lt;session row id&gt;/&lt;statement fingerprint&gt;</c>, and one level
/// deeper for a subagent: <c>/sessions/&lt;id&gt;/&lt;agent&gt;/&lt;fingerprint&gt;</c>. The
/// row id rather than the host's session string because a session id is opaque text that may
/// contain anything, including a separator, and a path segment that invents a level of
/// hierarchy would silently reparent a whole session's notes. Slugging it instead would let
/// two distinct sessions collide on one segment, which at the fingerprint leaf is not a
/// display problem but one note superseding an unrelated one.
/// </para>
/// </remarks>
public static class SessionFacts
{
    public const string Root = "/sessions";

    public const string Predicate = "noted";

    public const string Scope = "session";

    /// <summary>
    /// An agent worked this out during a session, so it is <c>observed</c> — not
    /// <c>stated</c>, which is reserved for the user's own words, and not <c>inferred</c>,
    /// which would rank a note taken from real command output below one the user typed in
    /// passing. Not regenerable: nothing can recompute what an agent concluded once the
    /// session it concluded it in is gone (D23).
    /// </summary>
    public const string LearnedVia = "observed";

    public const string NoteKind = "note";

    public const string SessionKind = "session";

    public const string AgentKind = "agent";

    public static string SessionPath(long sessionId) =>
        Root + "/" + sessionId.ToString(CultureInfo.InvariantCulture);

    public static string AgentPath(long sessionId, string agent) =>
        SessionPath(sessionId) + "/" + CannedFactSeeder.Slug(agent);

    public static string PathFor(long sessionId, string? agent, string statement)
    {
        var prefix = string.IsNullOrWhiteSpace(agent) ? SessionPath(sessionId) : AgentPath(sessionId, agent);
        return prefix + "/" + FactStore.Fingerprint(statement);
    }

    /// <summary>
    /// Records a note and returns its fact id — or the id of the note already stored, if this
    /// session has recorded that statement before.
    /// </summary>
    /// <remarks>
    /// A repeat returns the existing handle rather than writing again or superseding. An
    /// agent re-recording something it already recorded has learned nothing new, and the
    /// alternative is a supersession row asserting that a belief changed when the text is
    /// identical.
    /// </remarks>
    public static long Append(
        SqliteConnection connection,
        string sessionExternalId,
        string statement,
        string? subject,
        string? evidence,
        string? agent,
        DateTimeOffset now)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);

        var sessionId = SessionStore.EnsureSession(connection, transaction, sessionExternalId, now);
        var path = PathFor(sessionId, agent, statement);

        if (FactStore.FindLiveFactId(connection, transaction, path, Predicate) is { } existing)
        {
            transaction.Rollback();
            return existing;
        }

        var createdAt = now.ToUnixTimeSeconds();
        FactStore.EnsureEntity(connection, transaction, SessionPath(sessionId), SessionKind, createdAt);

        if (!string.IsNullOrWhiteSpace(agent))
        {
            // Named, because the path only carries the slug and the model reads this back:
            // "task-gopher:task-gopher" and "task gopher task gopher" slug identically.
            FactStore.EnsureEntity(
                connection, transaction, AgentPath(sessionId, agent), AgentKind, createdAt, displayName: agent);
        }

        var result = FactStore.Remember(
            connection,
            transaction,
            new FactWrite(
                SubjectPath: path,
                SubjectKind: NoteKind,
                Predicate: Predicate,
                Body: statement,
                Scope: Scope,
                LearnedVia: LearnedVia,
                Evidence: evidence,
                Regenerable: false,
                SessionId: sessionId),
            now,
            reason: "re-recorded in the same session");

        // The subject the model supplied is display text for the note, so it goes on the
        // entity — but only if it gave one. Defaulting it to the statement would name the
        // entity after a body that supersession is free to replace.
        if (!string.IsNullOrWhiteSpace(subject))
        {
            SetEntityName(connection, transaction, path, subject);

            // TextFor reads the subject's current name, and Remember already indexed this fact
            // under the fingerprint default a few lines up — before the rename above gave it
            // one. Re-index now so fact_token reflects the name the model actually gave it,
            // rather than the address it happened to land at.
            FactTokenIndex.Remove(connection, transaction, result.FactId);
            FactTokenIndex.Add(connection, transaction, result.FactId);
        }

        transaction.Commit();
        return result.FactId;
    }

    /// <summary>
    /// Live notes, split into the current session's and every earlier session's.
    /// </summary>
    /// <remarks>
    /// One subtree read partitioned in memory rather than two queries, because "everything
    /// except one session" is not a range and would be a scan either way.
    /// </remarks>
    public static (IReadOnlyList<SessionFact> Current, IReadOnlyList<SessionFact> Prior) Read(
        SqliteConnection connection,
        string? currentSessionExternalId,
        DateTimeOffset now)
    {
        // Looked up rather than ensured: recall is a read, and a read that creates a session
        // row would make every query a write against the same lock the writers contend for.
        var currentSessionId = currentSessionExternalId is { Length: > 0 } external
            ? SessionStore.FindSession(connection, external)
            : null;

        var agentNames = FactStore.ReadEntityNames(connection, AgentKind);

        var current = new List<SessionFact>();
        var prior = new List<SessionFact>();

        foreach (var stored in FactStore.ReadSubtree(connection, Root))
        {
            if (ToSessionFact(stored, agentNames, now) is not { } fact)
            {
                continue;
            }

            (fact.SessionId == currentSessionId ? current : prior).Add(fact);
        }

        return (current, prior);
    }

    private static SessionFact? ToSessionFact(
        StoredFact stored,
        IReadOnlyDictionary<string, string> agentNames,
        DateTimeOffset now)
    {
        // /sessions/<id>/<fingerprint>, or /sessions/<id>/<agent>/<fingerprint>.
        var segments = stored.SubjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is not (3 or 4)
            || !long.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId))
        {
            // A fact parked under this root by something that did not put it there through
            // Append. Skipping it is better than guessing at a session it may not belong to.
            return null;
        }

        string? agent = null;
        if (segments.Length == 4)
        {
            agentNames.TryGetValue("/" + segments[0] + "/" + segments[1] + "/" + segments[2], out agent);
            agent ??= segments[2];
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(stored.CreatedAt);

        return new SessionFact(
            FactId: stored.Id,
            SessionId: sessionId,
            Statement: stored.Body,
            // The entity is named after the path leaf when nothing named it, which is the
            // fingerprint — not a subject anyone wrote, so it is not reported as one.
            Subject: stored.SubjectName == segments[^1] ? null : stored.SubjectName,
            Agent: agent,
            AgeDays: age.TotalDays > 0 ? (int)age.TotalDays : 0);
    }

    private static void SetEntityName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE entity SET name = $name WHERE path = $path;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }
}
