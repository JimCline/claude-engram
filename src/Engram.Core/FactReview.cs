using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>One <c>fact_review</c> row.</summary>
public sealed record ReviewEntry(long FactId, long ReviewAfter, long SetAt);

/// <summary>
/// The review-due marker (docs/memory-expansion/04-lifecycle-spec.md): an explicit,
/// caller-supplied reminder date on a fact. Side-table, mirroring
/// <see cref="FactSyncRequests"/>'s pattern — never a column on <c>fact</c> (D8). Unlike
/// <c>fact_sync_request</c>, a fact may only carry one review date at a time, so setting a new
/// one replaces the row rather than accumulating.
/// </summary>
public static class FactReview
{
    /// <summary>Sets or replaces the review date for <paramref name="factId"/>.</summary>
    public static void Set(
        SqliteConnection connection, SqliteTransaction? transaction, long factId, long reviewAfter, long setAt)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO fact_review (fact_id, review_after, set_at) VALUES ($factId, $reviewAfter, $setAt)
              ON CONFLICT(fact_id) DO UPDATE SET review_after = excluded.review_after, set_at = excluded.set_at;
            """;
        command.Parameters.AddWithValue("$factId", factId);
        command.Parameters.AddWithValue("$reviewAfter", reviewAfter);
        command.Parameters.AddWithValue("$setAt", setAt);
        command.ExecuteNonQuery();
    }

    /// <summary>Clears the review marker for <paramref name="factId"/>. Returns whether a row was removed.</summary>
    public static bool Clear(SqliteConnection connection, SqliteTransaction? transaction, long factId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM fact_review WHERE fact_id = $factId;";
        command.Parameters.AddWithValue("$factId", factId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Every review marker, due soonest first, joined against its fact's live body.</summary>
    public static IReadOnlyList<(ReviewEntry Entry, string Body)> ListLive(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.fact_id, r.review_after, r.set_at, f.body
              FROM fact_review r
              JOIN fact f ON f.id = r.fact_id
             WHERE f.valid_to IS NULL
             ORDER BY r.review_after;
            """;

        var rows = new List<(ReviewEntry, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                new ReviewEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)),
                reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Live facts whose review date has passed. Shares the "join against live fact" shape with
    /// <see cref="ListLive"/> rather than a second query, since the only difference is the
    /// <c>review_after &lt;= $now</c> filter and the count.
    /// </summary>
    public static int CountDue(SqliteConnection connection, long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
              FROM fact_review r
              JOIN fact f ON f.id = r.fact_id
             WHERE f.valid_to IS NULL AND r.review_after <= $now;
            """;
        command.Parameters.AddWithValue("$now", nowUnixSeconds);
        return (int)(long)command.ExecuteScalar()!;
    }
}
