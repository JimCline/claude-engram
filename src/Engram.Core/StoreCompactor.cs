using Microsoft.Data.Sqlite;

namespace Engram.Core;

public sealed record CompactReport(
    bool Applied,
    string? Path,
    int LivePruned,
    int ClosedPruned,
    int ProtectedByAuthoredHistory,
    int SupersessionsRemoved,
    int VectorsRemoved,
    int EntitiesRemoved,
    int FileStatesRemoved,
    int ReposDeregistered,
    string? SnapshotName,
    bool Vacuumed,
    IReadOnlyList<string> Notes)
{
    public int FactsPruned => LivePruned + ClosedPruned;

    public bool AnythingToPrune =>
        FactsPruned > 0 || SupersessionsRemoved > 0 || VectorsRemoved > 0
        || EntitiesRemoved > 0 || FileStatesRemoved > 0 || ReposDeregistered > 0;
}

/// <summary>
/// Prunes what can be regenerated, and only that (D8): rows with <c>regenerable = 1</c>,
/// keyed off that column alone and never <c>learned_via</c> or where a fact lives (D23).
/// Without <c>--path</c> it prunes closed regenerable facts — superseded index history,
/// which a re-index can produce again. With a path prefix it prunes the whole regenerable
/// subtree, live rows included: the detached-repo case, where the source is gone and the
/// facts describe files nobody can open.
/// </summary>
/// <remarks>
/// <para>A regenerable fact that shares a supersession edge with an authored fact is
/// excluded in both directions: revising a code fact into a belief, or correcting a belief
/// with one, makes the pair part of authored history, and deleting either half leaves the
/// survivor explaining a revision nobody can read.</para>
///
/// <para>Path mode also clears <c>file_state</c> for every file the prefix touches, and that
/// is load-bearing rather than tidy: pruned facts with surviving file state would make the
/// next index run see unchanged blob hashes and rewrite nothing — a silent, permanent loss.
/// A file's state goes when its facts go <em>or</em> when the prefix names something inside
/// it, because either way the file must be re-read to be whole again.</para>
///
/// <para>Deleting a closed fact requires schema version 3: the version 2 delete trigger
/// re-deleted an FTS entry <c>fact_fts_close</c> had already removed, and FTS5 fails the
/// statement with "database disk image is malformed".</para>
/// </remarks>
public static class StoreCompactor
{
    public static CompactReport Compact(
        SqliteConnection connection,
        EngramHome home,
        string? path,
        bool apply,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);

        var prefix = path is null || path.Length <= 1 ? path : path.TrimEnd('/');
        var notes = new List<string>();

        Execute(connection, null, "DROP TABLE IF EXISTS temp.compact_target;");
        try
        {
            SelectTargets(connection, prefix);

            var livePruned = Scalar(connection, "SELECT count(*) FROM temp.compact_target WHERE was_live = 1;");
            var closedPruned = Scalar(connection, "SELECT count(*) FROM temp.compact_target WHERE was_live = 0;");
            var protectedCount = CountProtected(connection, prefix);

            var supersessions = Scalar(
                connection,
                "SELECT count(*) FROM supersession WHERE old_fact_id IN (SELECT id FROM temp.compact_target) "
                + "OR new_fact_id IN (SELECT id FROM temp.compact_target);");

            // Orphaned vectors — rows whose fact is already gone — are swept alongside the
            // targets (D36 makes stray vector rows compact's problem). Counting touches the
            // vec0 module, which is loaded per connection and may simply not be installed;
            // that costs the sweep and nothing else.
            var vectors = 0;
            var vectorsCountable = false;
            if (VectorIndex.Exists(connection))
            {
                try
                {
                    vectors = Scalar(
                        connection,
                        $"SELECT count(*) FROM {VectorIndex.TableName} "
                        + "WHERE fact_id IN (SELECT id FROM temp.compact_target) "
                        + "OR fact_id NOT IN (SELECT id FROM fact);");
                    vectorsCountable = true;
                }
                catch (SqliteException e) when (e.Message.Contains("no such module", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add("a vector index exists but sqlite-vec is not loaded here; its rows were left alone");
                }
            }

            var entities = 0;
            var fileStates = 0;
            var repos = 0;
            if (prefix is not null)
            {
                entities = Scalar(connection, EntitySql("SELECT count(*) FROM entity"), ("$p", prefix));
                fileStates = Scalar(connection, FileStateSql("SELECT count(*) FROM file_state"), ("$p", prefix));
                repos = Scalar(connection, RegistrySql("SELECT count(*) FROM repo_registry"), ("$p", prefix));
            }

            var report = new CompactReport(
                Applied: false,
                prefix,
                livePruned,
                closedPruned,
                protectedCount,
                supersessions,
                vectors,
                entities,
                fileStates,
                repos,
                SnapshotName: null,
                Vacuumed: false,
                notes);

            if (!apply)
            {
                return report;
            }

            var snapshot = BackupStore.Take(connection, home, now, label: "pre-compact");

            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM supersession WHERE old_fact_id IN (SELECT id FROM temp.compact_target) "
                    + "OR new_fact_id IN (SELECT id FROM temp.compact_target);");

                // A surviving fact may point into the target through a regenerable-only
                // chain; the pointer has to go before the row it names does, or the delete
                // trips foreign keys. Its valid_to stays — clearing the pointer does not
                // reopen the belief.
                Execute(
                    connection,
                    transaction,
                    "UPDATE fact SET superseded_by = NULL "
                    + "WHERE superseded_by IN (SELECT id FROM temp.compact_target) "
                    + "AND id NOT IN (SELECT id FROM temp.compact_target);");

                Execute(connection, transaction, "DELETE FROM fact WHERE id IN (SELECT id FROM temp.compact_target);");

                if (vectorsCountable && vectors > 0)
                {
                    Execute(
                        connection,
                        transaction,
                        $"DELETE FROM {VectorIndex.TableName} WHERE fact_id NOT IN (SELECT id FROM fact);");
                }

                if (prefix is not null)
                {
                    Execute(connection, transaction, EntitySql("DELETE FROM entity"), ("$p", prefix));
                    Execute(connection, transaction, FileStateSql("DELETE FROM file_state"), ("$p", prefix));
                    Execute(connection, transaction, RegistrySql("DELETE FROM repo_registry"), ("$p", prefix));
                }

                transaction.Commit();
            }

            Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, null, "VACUUM;");

            return report with
            {
                Applied = true,
                SnapshotName = Path.GetFileName(snapshot.Path),
                Vacuumed = true,
            };
        }
        finally
        {
            // Temp schema, so nothing in the store file — dropped anyway, because the
            // connection may be a pooled one with a long life ahead of it.
            Execute(connection, null, "DROP TABLE IF EXISTS temp.compact_target;");
        }
    }

    private static void SelectTargets(SqliteConnection connection, string? prefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TEMP TABLE compact_target AS "
            + "SELECT id, valid_to IS NULL AS was_live FROM fact "
            + $"WHERE regenerable = 1 AND {ModeSql(prefix)} AND NOT {AuthoredEdgeSql};";
        if (prefix is not null)
        {
            command.Parameters.AddWithValue("$p", prefix);
        }

        command.ExecuteNonQuery();
    }

    private static int CountProtected(SqliteConnection connection, string? prefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count(*) FROM fact WHERE regenerable = 1 AND {ModeSql(prefix)} AND {AuthoredEdgeSql};";
        if (prefix is not null)
        {
            command.Parameters.AddWithValue("$p", prefix);
        }

        return (int)(long)command.ExecuteScalar()!;
    }

    private static string ModeSql(string? prefix) =>
        prefix is null ? "valid_to IS NOT NULL" : Subtree("fact.path");

    /// <summary>An edge in either direction to a fact that cannot be regenerated.</summary>
    private const string AuthoredEdgeSql =
        "EXISTS (SELECT 1 FROM supersession s "
        + "JOIN fact other ON other.id = "
        + "CASE WHEN s.old_fact_id = fact.id THEN s.new_fact_id ELSE s.old_fact_id END "
        + "WHERE (s.old_fact_id = fact.id OR s.new_fact_id = fact.id) "
        + "AND other.regenerable = 0)";

    /// <summary>
    /// <paramref name="column"/> equals the prefix or sits under it, where "under" crosses
    /// a segment boundary — <c>/</c> into a subtree, <c>#</c> into a file's symbols — so
    /// <c>/code/api</c> never captures <c>/code/api-v2</c>.
    /// </summary>
    private static string Subtree(string column) =>
        $"({column} = $p OR (substr({column}, 1, length($p)) = $p "
        + $"AND substr({column}, length($p) + 1, 1) IN ('/', '#')))";

    private static string EntitySql(string head) =>
        $"{head} WHERE kind IN ('repo', 'module', 'file', 'symbol', 'section') "
        + $"AND {Subtree("entity.path")} "
        // Written to hold before and after the fact delete: in a dry run the targets still
        // exist, so "no remaining reference" has to look through them.
        + "AND NOT EXISTS (SELECT 1 FROM fact "
        + "WHERE (fact.subject_id = entity.id OR fact.object_id = entity.id) "
        + "AND fact.id NOT IN (SELECT id FROM temp.compact_target));";

    /// <summary>
    /// A file's memory path is <c>repo_path || '/' || path</c> (the grammar's ForFile). The
    /// prefix relation runs both ways: the file under the prefix, or the prefix naming a
    /// symbol inside the file — either way the file must be re-read on the next index run.
    /// </summary>
    private static string FileStateSql(string head) =>
        $"{head} WHERE {Subtree("(repo_path || '/' || path)")} "
        + "OR (substr($p, 1, length(repo_path || '/' || path)) = (repo_path || '/' || path) "
        + "AND substr($p, length(repo_path || '/' || path) + 1, 1) IN ('/', '#'));";

    private static string RegistrySql(string head) =>
        $"{head} WHERE repo_path = $p "
        + "OR (substr(repo_path, 1, length($p)) = $p AND substr(repo_path, length($p) + 1, 1) = '/');";

    private static int Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (int)(long)command.ExecuteScalar()!;
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
