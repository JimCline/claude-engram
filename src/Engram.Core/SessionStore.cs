using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The provenance anchor: the row every fact's <c>session_id</c> points at.
/// </summary>
/// <remarks>
/// The host's session identifier is a string and the fact column is a foreign key, so the
/// two cannot be the same value. <c>session.external_id</c> is the bridge, and it is UNIQUE
/// so a session that appears in twenty facts is still one row.
/// </remarks>
public static class SessionStore
{
    public const string ClaudeCodeHost = "claude-code";

    /// <summary>
    /// The row id for a host session identifier, or null if that session has never written.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EnsureSession"/> so read paths have something to call. A
    /// read that creates a session row would put every recall behind the write lock, and
    /// would leave a row for every session that only ever asked a question.
    /// </remarks>
    public static long? FindSession(SqliteConnection connection, string externalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM session WHERE external_id = $external;";
        command.Parameters.AddWithValue("$external", externalId);

        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>
    /// Returns the row id for a host session identifier, creating the row if this is the
    /// first thing that session has written.
    /// </summary>
    public static long EnsureSession(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string externalId,
        DateTimeOffset startedAt,
        string host = ClaudeCodeHost)
    {
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT id FROM session WHERE external_id = $external;";
            lookup.Parameters.AddWithValue("$external", externalId);

            if (lookup.ExecuteScalar() is { } existing and not DBNull)
            {
                return Convert.ToInt64(existing);
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO session (external_id, host, started_at) VALUES ($external, $host, $started);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$external", externalId);
        insert.Parameters.AddWithValue("$host", host);
        insert.Parameters.AddWithValue("$started", startedAt.ToUnixTimeSeconds());

        return Convert.ToInt64(insert.ExecuteScalar()!);
    }

    /// <summary>
    /// Records a session's closing summary, creating the session row if nothing else has
    /// written for it yet.
    /// </summary>
    /// <remarks>
    /// This overwrites, which is deliberate and is not the append-only rule bending. That
    /// rule governs belief content — a fact's predicate, body, and validity window. A session
    /// summary is neither: it is one description of one session, and `engram_digest` may fire
    /// twice in a session (once at compaction, once at the end), where the later call has seen
    /// strictly more. Keeping the first would pin the summary to the least-informed moment.
    /// The learnings themselves go through <see cref="SessionFacts"/> and stay append-only.
    /// </remarks>
    public static long WriteDigest(
        SqliteConnection connection,
        string externalId,
        string digest,
        DateTimeOffset now)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        var sessionId = EnsureSession(connection, transaction, externalId, now);

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE session SET digest = $digest WHERE id = $id;";
            update.Parameters.AddWithValue("$digest", digest);
            update.Parameters.AddWithValue("$id", sessionId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return sessionId;
    }
}
