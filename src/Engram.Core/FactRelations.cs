using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>One judged relationship between two facts, as <c>fact_relation</c> stores it.</summary>
public sealed record StoredRelation(
    long Id, long FactId, long RelatedId, string Relation, string? Reason, long JudgedAt);

/// <summary>
/// Conflict verdicts (docs/memory-expansion/02-conflict-verdicts-spec.md): an annotation over two
/// facts, never a mutation of either (D8). A verdict is written once and never revised — closing
/// or superseding it would need a second verdict, not an edit to this one.
/// </summary>
public static class FactRelations
{
    /// <summary>The only values <c>fact_relation.relation</c> accepts, matching the schema's CHECK.</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "supersedes",
        "conflicts_with",
        "scoped",
        "not_conflict",
    };

    /// <summary>
    /// Writes one immutable <c>fact_relation</c> row. Throws <see cref="ArgumentException"/> for a
    /// relation outside <see cref="Kinds"/> rather than relying on the schema's CHECK constraint to
    /// reject it, so a bad call fails with a message naming the field instead of a raw SQLite error.
    /// </summary>
    public static StoredRelation Judge(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long factId,
        long relatedId,
        string relation,
        string? reason,
        long judgedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!Kinds.Contains(relation))
        {
            throw new ArgumentException(
                $"relation must be one of {string.Join(", ", Kinds)}, got '{relation}'.", nameof(relation));
        }

        if (factId == relatedId)
        {
            throw new ArgumentException("a fact cannot be judged against itself.", nameof(relatedId));
        }

        var id = Insert(connection, transaction, factId, relatedId, relation, reason, judgedAt);
        return new StoredRelation(id, factId, relatedId, relation, reason, judgedAt);
    }

    /// <summary>
    /// Raw insert, no relation-value validation — the schema's CHECK constraint is the backstop.
    /// Used directly by <see cref="RelationJournal.Replay"/>, whose rows were already validated
    /// once, when they were written.
    /// </summary>
    public static long Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long factId,
        long relatedId,
        string relation,
        string? reason,
        long judgedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO fact_relation (fact_id, related_id, relation, reason, judged_at)
            VALUES ($factId, $relatedId, $relation, $reason, $judgedAt)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$factId", factId);
        command.Parameters.AddWithValue("$relatedId", relatedId);
        command.Parameters.AddWithValue("$relation", relation);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$judgedAt", judgedAt);

        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Whether a row already matches this fact pair, relation and timestamp — the identity a
    /// replay treats as "already present" rather than inserting a duplicate (mirroring D32's
    /// fact-replay idempotency, which matches on the belief's own content rather than a raw id).
    /// </summary>
    public static bool Exists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long factId,
        long relatedId,
        string relation,
        long judgedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1 FROM fact_relation
             WHERE fact_id = $factId AND related_id = $relatedId
               AND relation = $relation AND judged_at = $judgedAt
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$factId", factId);
        command.Parameters.AddWithValue("$relatedId", relatedId);
        command.Parameters.AddWithValue("$relation", relation);
        command.Parameters.AddWithValue("$judgedAt", judgedAt);

        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Every <c>fact_relation</c> row that names <paramref name="factId"/> on either side, newest
    /// first — what <c>engram_expand ... history</c> renders alongside a fact's version thread.
    /// </summary>
    public static IReadOnlyList<StoredRelation> ForFact(SqliteConnection connection, long factId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, fact_id, related_id, relation, reason, judged_at
              FROM fact_relation
             WHERE fact_id = $factId OR related_id = $factId
             ORDER BY judged_at DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$factId", factId);

        var relations = new List<StoredRelation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            relations.Add(new StoredRelation(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5)));
        }

        return relations;
    }

    /// <summary>
    /// How many <c>fact_relation</c> rows reference each fact, either side, keyed by fact id — the
    /// source of recall's <c>· judged</c> marker (D57's <c>· v2</c> pattern, one grouped query
    /// rather than a lookup per candidate).
    /// </summary>
    public static IReadOnlyDictionary<long, int> RelationCounts(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, COUNT(*) FROM (
              SELECT fact_id AS id FROM fact_relation
              UNION ALL
              SELECT related_id AS id FROM fact_relation
            )
            GROUP BY id;
            """;

        var counts = new Dictionary<long, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetInt64(0)] = reader.GetInt32(1);
        }

        return counts;
    }
}
