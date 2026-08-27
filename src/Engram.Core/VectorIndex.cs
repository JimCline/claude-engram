using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>One fact awaiting a vector, and the text that should become one.</summary>
public readonly record struct PendingEmbedding(long FactId, string Text);

/// <summary>A KNN hit: which fact, and how far from the query.</summary>
public readonly record struct VectorMatch(long FactId, double Distance);

/// <summary>
/// The <c>vec0</c> index over fact bodies — creation, backfill, retirement, and search.
/// </summary>
/// <remarks>
/// <para><b>Not in the schema file, on purpose.</b> <c>docs/engram-schema.sql</c> is the
/// authority for database shape, and this table is the one thing it cannot hold: the DDL
/// embeds the vector width, which is a property of whichever embedder is configured, and
/// applying it at all requires <c>sqlite-vec</c> to be loaded on that connection. A static
/// statement could express neither. So the schema file documents that this table exists and
/// defers to here for its shape.</para>
///
/// <para><b>Derived state, entirely.</b> Everything here can be rebuilt from <c>fact</c> and an
/// embedder, which is what makes <c>compact</c> and <c>repair</c> allowed to touch it under D8
/// and what makes dropping it a supported recovery rather than data loss.</para>
/// </remarks>
public static class VectorIndex
{
    public const string TableName = "fact_vec";

    public const string ModelKey = "embedding_model";
    public const string DimensionsKey = "embedding_dimensions";
    public const string InputKey = "embedding_input";

    /// <summary>
    /// What text was fed to the embedder, versioned.
    /// </summary>
    /// <remarks>
    /// Pinned alongside the model because it is an independent way to invalidate the index and
    /// the easy one to forget. Two vectors made by the same model from different text — body
    /// alone versus predicate plus body — are as incomparable as two models, and nothing about
    /// the stored vector reveals which it was. Changing the composition below means changing
    /// this string, which turns a silent inconsistency into a detected mismatch.
    /// </remarks>
    public const string InputVersion = "body/v1";

    /// <summary>The text embedded for a fact. Change this and <see cref="InputVersion"/>.</summary>
    public static string InputFor(string body) => body;

    public static bool Exists(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", TableName);

        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Creates the index and pins the space it holds. Requires <c>sqlite-vec</c> on this
    /// connection; throws <see cref="SqliteException"/> if it is absent, rather than pretending.
    /// </summary>
    public static void EnsureCreated(SqliteConnection connection, EmbeddingSpace space)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (Exists(connection))
        {
            return;
        }

        using var transaction = EngramDatabase.BeginWrite(connection);

        // `is_live` mirrors `fact.valid_to IS NULL`, and it is here rather than derived by a
        // join because a join cannot do the job: vec0 applies `k` before any join, so filtering
        // afterwards discards nearest neighbours that were already counted against the budget
        // and silently returns short. Measured — with the four nearest facts closed, a
        // post-filtered query for five live facts returned one. The filter has to be inside the
        // MATCH, so the column has to be inside the table.
        Execute(
            connection,
            transaction,
            $"""
             CREATE VIRTUAL TABLE {TableName} USING vec0(
                 fact_id INTEGER PRIMARY KEY,
                 is_live INTEGER,
                 embedding float[{space.Dimensions}] distance_metric=cosine
             );
             """);

        EngramDatabase.WriteMeta(connection, transaction, ModelKey, space.Model);
        EngramDatabase.WriteMeta(
            connection,
            transaction,
            DimensionsKey,
            space.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        EngramDatabase.WriteMeta(connection, transaction, InputKey, InputVersion);

        transaction.Commit();
    }

    /// <summary>
    /// The space this index actually holds, or null if it has never been created.
    /// </summary>
    /// <remarks>
    /// Callers compare this against their embedder's space and degrade to FTS5 on a mismatch
    /// rather than refusing to open — recall works without the vector lane, and an instance
    /// that will not start because an optional accelerator changed is worse than one that
    /// answers and says so. Width defends itself at the row level; the model does not, which is
    /// the whole reason it is recorded.
    /// </remarks>
    public static EmbeddingSpace? ReadSpace(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var model = EngramDatabase.ReadMeta(connection, ModelKey);
        var dimensions = EngramDatabase.ReadMeta(connection, DimensionsKey);

        if (string.IsNullOrEmpty(model)
            || !int.TryParse(
                dimensions,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var width)
            || width <= 0)
        {
            return null;
        }

        return new EmbeddingSpace(model, width);
    }

    /// <summary>The recorded input composition, or null if none was pinned.</summary>
    public static string? ReadInputVersion(SqliteConnection connection) =>
        EngramDatabase.ReadMeta(connection, InputKey);

    /// <summary>
    /// Live facts with no vector yet, oldest first.
    /// </summary>
    /// <remarks>
    /// The queue is this query and there is no queue table, which is the right shape under D8
    /// for a reason beyond economy: a derived list recomputed from a join cannot drift from
    /// what it describes, and a queue table can. It also makes failure free — an embedder that
    /// cannot handle one text returns null for it, nothing is written, and the fact is simply
    /// still here on the next pass. That is why <see cref="IEmbedder"/> may return nulls
    /// instead of throwing, and why callers must never store a placeholder vector to mark a
    /// failure: doing so would remove the fact from this queue forever.
    /// </remarks>
    public static IReadOnlyList<PendingEmbedding> ReadBackfillBatch(
        SqliteConnection connection,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT f.id, f.body
             FROM fact f
             LEFT JOIN {TableName} v ON v.fact_id = f.id
             WHERE f.valid_to IS NULL AND v.fact_id IS NULL
               AND NOT (f.regenerable IS 1 AND f.object_id IS NOT NULL)
             ORDER BY f.id
             LIMIT $limit;
             """;
        command.Parameters.AddWithValue("$limit", limit);

        var pending = new List<PendingEmbedding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            pending.Add(new PendingEmbedding(reader.GetInt64(0), InputFor(reader.GetString(1))));
        }

        return pending;
    }

    /// <summary>How many facts are still waiting for a vector.</summary>
    public static int CountPending(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT COUNT(*)
             FROM fact f
             LEFT JOIN {TableName} v ON v.fact_id = f.id
             WHERE f.valid_to IS NULL AND v.fact_id IS NULL
               AND NOT (f.regenerable IS 1 AND f.object_id IS NOT NULL);
             """;

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How many facts want a vector at all, whether or not the index exists.
    /// </summary>
    /// <remarks>
    /// The same eligibility predicate as <see cref="CountPending"/> and
    /// <see cref="ReadBackfillBatch"/> with the index side of the join removed, which is the only
    /// form of the question a rebuild can ask: it needs the count for a table it is about to
    /// throw away, and <see cref="CountPending"/> against a dropped table is a SQL error rather
    /// than an answer.
    /// </remarks>
    public static int CountEmbeddable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM fact WHERE valid_to IS NULL "
            + "AND NOT (regenerable IS 1 AND object_id IS NOT NULL);";

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stores one fact's vector, replacing any it already had.
    /// </summary>
    /// <remarks>
    /// Delete-then-insert, because <c>INSERT OR REPLACE</c> does not work on a <c>vec0</c>
    /// table — measured, it raises a primary-key uniqueness error rather than replacing, and it
    /// does so only on the second write for a given fact, which is exactly late enough to ship.
    /// The delete is unconditional so there is one path whether or not a row was there.
    ///
    /// <para><c>is_live</c> is read from the fact rather than supplied by the caller, so it
    /// cannot disagree with the fact it describes — a fact superseded between a backfill read
    /// and this write would otherwise be indexed as live and rank forever.</para>
    /// </remarks>
    public static void Write(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long factId,
        float[] vector)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(vector);

        var live = ReadLiveness(connection, transaction, factId);
        if (live is null)
        {
            // The fact was deleted outright. Writing a vector for it would strand a row no
            // query can reach and no rebuild would recreate.
            return;
        }

        Execute(
            connection,
            transaction,
            $"DELETE FROM {TableName} WHERE fact_id = $id;",
            ("$id", factId));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {TableName} (fact_id, is_live, embedding) VALUES ($id, $live, $v);";
        command.Parameters.AddWithValue("$id", factId);
        command.Parameters.AddWithValue("$live", live.Value ? 1 : 0);
        command.Parameters.AddWithValue("$v", ToBlob(vector));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks a fact's vector as no longer current, without discarding it.
    /// </summary>
    /// <remarks>
    /// An update rather than a delete, so the history a superseded fact represents stays
    /// searchable when something explicitly asks for it. The cost is that the index grows with
    /// every supersession, which is `compact`'s problem to solve — it may prune retired vectors
    /// because they are derived state (D8), and rebuilding one costs an embedding.
    ///
    /// <para>A no-op when there is no index, rather than an error. Embeddings are optional under
    /// D18, so supersession — which is authored truth — must not be able to fail because an
    /// accelerator is absent. This is the one call the fact path makes into here, and it is the
    /// one that has to be unconditional.</para>
    /// </remarks>
    public static void Retire(SqliteConnection connection, SqliteTransaction? transaction, long factId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!Exists(connection))
        {
            return;
        }

        Execute(
            connection,
            transaction,
            $"UPDATE {TableName} SET is_live = 0 WHERE fact_id = $id;",
            ("$id", factId));
    }

    /// <summary>
    /// Brings <c>is_live</c> back in line with <c>fact.valid_to</c>, and drops vectors whose
    /// fact is gone. Returns how many rows it touched.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists instead of a call in <c>FactStore</c>.</b> Retiring a vector at
    /// supersession time is the obvious design and it is wrong here, because it would put a
    /// <c>vec0</c> statement on the write path: an instance whose <c>lib/</c> went missing has
    /// a <c>fact_vec</c> table it can no longer address, and every <c>remember</c> would start
    /// failing with <c>no such module: vec0</c>. Authored truth would then depend on an optional
    /// accelerator, which is the one thing D18 says it must not. Catching and ignoring the error
    /// is worse: it leaves the index silently stale with no record that it is.</para>
    ///
    /// <para>So liveness is reconciled instead of maintained, on the same pass that fills the
    /// index. Staleness is bounded by how often that pass runs, and it is bounded in the safe
    /// direction — a stale row means a superseded fact can still be returned, never that a live
    /// one is hidden.</para>
    /// </remarks>
    public static int Reconcile(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!Exists(connection))
        {
            return 0;
        }

        using var transaction = EngramDatabase.BeginWrite(connection);

        var touched = 0;
        foreach (var factId in ReadIds(
            connection,
            transaction,
            $"""
             SELECT v.fact_id FROM {TableName} v
             JOIN fact f ON f.id = v.fact_id
             WHERE v.is_live = 1 AND f.valid_to IS NOT NULL;
             """))
        {
            Execute(
                connection,
                transaction,
                $"UPDATE {TableName} SET is_live = 0 WHERE fact_id = $id;",
                ("$id", factId));
            touched++;
        }

        // Facts are append-only, so an orphan means `compact` pruned one. Repairing derived
        // state is exactly what that leaves behind for someone else to do (D8).
        foreach (var factId in ReadIds(
            connection,
            transaction,
            $"""
             SELECT v.fact_id FROM {TableName} v
             LEFT JOIN fact f ON f.id = v.fact_id
             WHERE f.id IS NULL;
             """))
        {
            Execute(
                connection,
                transaction,
                $"DELETE FROM {TableName} WHERE fact_id = $id;",
                ("$id", factId));
            touched++;
        }

        transaction.Commit();

        return touched;
    }

    /// <summary>Nearest live facts to <paramref name="query"/>, closest first.</summary>
    public static IReadOnlyList<VectorMatch> Search(
        SqliteConnection connection,
        float[] query,
        int k)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        using var command = connection.CreateCommand();

        // The liveness filter rides inside the MATCH rather than in a join for the reason
        // EnsureCreated records: after the join is too late.
        command.CommandText =
            $"""
             SELECT v.fact_id, v.distance
             FROM {TableName} v
             WHERE v.embedding MATCH $q AND v.k = $k AND v.is_live = 1
             ORDER BY v.distance;
             """;
        command.Parameters.AddWithValue("$q", ToBlob(query));
        command.Parameters.AddWithValue("$k", k);

        var matches = new List<VectorMatch>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            matches.Add(new VectorMatch(reader.GetInt64(0), reader.GetDouble(1)));
        }

        return matches;
    }

    /// <summary>Total rows, or only those still marked live.</summary>
    public static int Count(SqliteConnection connection, bool liveOnly = false)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = liveOnly
            ? $"SELECT COUNT(*) FROM {TableName} WHERE is_live = 1;"
            : $"SELECT COUNT(*) FROM {TableName};";

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Empties the index, keeping its shape. The same-width half of <c>--rebuild</c>.</summary>
    public static void Clear(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Execute(connection, transaction, $"DELETE FROM {TableName};");
    }

    /// <summary>
    /// Vectors whose fact is no longer lane-eligible (edge-fact-lane-eligibility.md §3.3),
    /// grouped by predicate — the dry-run counts for <c>embed --prune</c>. Grouping is not
    /// cosmetic: it is what shows the reader that most of a delete is pre-existing
    /// <c>calls</c>/<c>imports</c> vectors, not the four newer predicates.
    /// </summary>
    public static IReadOnlyList<(string Predicate, int Count)> CountIneligibleByPredicate(
        SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT f.predicate, COUNT(*)
             FROM {TableName} v
             JOIN fact f ON f.id = v.fact_id
             WHERE f.regenerable IS 1 AND f.object_id IS NOT NULL
             GROUP BY f.predicate
             ORDER BY f.predicate;
             """;

        var counts = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return counts;
    }

    /// <summary>
    /// Deletes vectors whose fact is no longer lane-eligible. <see cref="Clear"/>-shaped — a
    /// targeted <c>DELETE</c>, never <see cref="Drop"/> — so the table's space pin survives,
    /// because nothing about the embedding space changed (edge-fact-lane-eligibility.md §3.3).
    /// Every eligible vector stays untouched.
    /// </summary>
    public static int PruneIneligible(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             DELETE FROM {TableName}
             WHERE fact_id IN (SELECT id FROM fact WHERE regenerable IS 1 AND object_id IS NOT NULL);
             """;

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes the index entirely, along with the space it pinned. The other half of
    /// <c>--rebuild</c>: a width change invalidates the table itself, not merely its rows.
    /// </summary>
    public static void Drop(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var transaction = EngramDatabase.BeginWrite(connection);

        Execute(connection, transaction, $"DROP TABLE IF EXISTS {TableName};");
        foreach (var key in new[] { ModelKey, DimensionsKey, InputKey })
        {
            Execute(
                connection,
                transaction,
                "DELETE FROM schema_meta WHERE key = $key;",
                ("$key", key));
        }

        transaction.Commit();
    }

    private static List<long> ReadIds(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static bool? ReadLiveness(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long factId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT valid_to IS NULL FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);

        var result = command.ExecuteScalar();
        return result is null or DBNull
            ? null
            : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
