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
}
