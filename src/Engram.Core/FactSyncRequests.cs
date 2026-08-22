using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The explicit, per-fact "always sync regardless of scope" opt-in
/// (docs/memory-expansion/01-sync-spec.md, "Per-fact opt-in"). Insert-only, mirroring
/// <see cref="FactRelations"/>'s pattern — never a column on <c>fact</c> (D8). Unlike
/// <c>sync_chunk_state</c>/<c>sync_deferred_close</c>, not rebuildable: nothing about a fact's
/// content or history says whether its author asked for it to always sync, so it is the one
/// authoritative record of that decision, and losing a row is a real loss.
/// </summary>
public static class FactSyncRequests
{
    /// <summary>Flags <paramref name="factId"/> for always-sync. Throws if it is already flagged.</summary>
    public static void Insert(SqliteConnection connection, SqliteTransaction? transaction, long factId, long requestedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO fact_sync_request (fact_id, requested_at) VALUES ($factId, $requestedAt);";
        command.Parameters.AddWithValue("$factId", factId);
        command.Parameters.AddWithValue("$requestedAt", requestedAt);
        command.ExecuteNonQuery();
    }

    /// <summary>Whether <paramref name="factId"/> currently carries an always-sync flag.</summary>
    public static bool IsFlagged(SqliteConnection connection, SqliteTransaction? transaction, long factId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM fact_sync_request WHERE fact_id = $factId LIMIT 1;";
        command.Parameters.AddWithValue("$factId", factId);

        return command.ExecuteScalar() is not null;
    }
}
