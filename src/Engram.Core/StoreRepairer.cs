using Microsoft.Data.Sqlite;

namespace Engram.Core;

public sealed record RepairReport(
    bool Applied,
    int LiveFacts,
    int FtsMissing,
    int FtsExtra,
    bool FtsCorrupt,
    bool FtsRebuilt,
    int PathsDrifted,
    int OrphanSalience,
    long WalBytes,
    bool Vacuumed,
    string? SnapshotName)
{
    public bool FtsNeedsRebuild => FtsMissing > 0 || FtsExtra > 0 || FtsCorrupt;

    public bool AnythingToFix => FtsNeedsRebuild || PathsDrifted > 0 || OrphanSalience > 0;
}

/// <summary>
/// Rebuilds what can be derived again, and only that (D8): the lexical index, the
/// denormalized <c>fact.path</c>, orphaned salience rows, the WAL. It may never create,
/// alter, or delete a fact body, predicate, validity window, or supersession row — if the
/// only fix would invent or destroy a belief, this reports and stops being the tool.
/// </summary>
public static class StoreRepairer
{
    /// <remarks>
    /// <para>The FTS rebuild goes through <see cref="EngramDatabase.RebuildFactFts"/> rather
    /// than FTS5's own <c>'rebuild'</c> command, and the difference is not stylistic:
    /// <c>fact_fts</c> is external-content over the whole <c>fact</c> table while the index
    /// deliberately holds only live facts, so <c>'rebuild'</c> would re-read every row —
    /// closed beliefs included — and recall would start returning superseded facts. One
    /// implementation of "what belongs in the index" exists, and repair calls it.</para>
    ///
    /// <para>Drift is detected through <c>fts5vocab</c>, and the detour is load-bearing: on
    /// an external-content table every non-MATCH query — including <c>SELECT rowid FROM
    /// fact_fts</c> — is answered from the content table, so the obvious set difference
    /// compares <c>fact</c> against itself and reads a desynced index as healthy. Measured
    /// here: the first version of this detector could not see its own test's planted
    /// desync. The vocab table enumerates the real index. The integrity check keeps
    /// <c>rank=0</c> — explicitly not the <c>rank=1</c> form, which compares against the
    /// content table and would read the live-only subset as corruption on any store that
    /// has ever closed a fact.</para>
    ///
    /// <para>Paths are re-derived before the index is rebuilt, so a rebuilt index reads
    /// corrected paths; when only paths drifted, the repath trigger resyncs the index and no
    /// rebuild is needed at all.</para>
    /// </remarks>
    public static RepairReport Repair(
        SqliteConnection connection,
        EngramHome home,
        bool apply,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);

        var liveFacts = Scalar(connection, "SELECT count(*) FROM fact WHERE valid_to IS NULL;");

        int ftsMissing;
        int ftsExtra;
        Execute(
            connection,
            "CREATE VIRTUAL TABLE IF NOT EXISTS temp.repair_fts_vocab USING fts5vocab('main', 'fact_fts', 'instance');");
        try
        {
            ftsMissing = Scalar(
                connection,
                "SELECT count(*) FROM (SELECT id FROM fact WHERE valid_to IS NULL "
                + "EXCEPT SELECT DISTINCT doc FROM temp.repair_fts_vocab);");
            ftsExtra = Scalar(
                connection,
                "SELECT count(*) FROM (SELECT DISTINCT doc FROM temp.repair_fts_vocab "
                + "EXCEPT SELECT id FROM fact WHERE valid_to IS NULL);");
        }
        finally
        {
            // Temp schema, so nothing in the store file — dropped anyway, because the
            // connection may be a pooled one with a long life ahead of it.
            Execute(connection, "DROP TABLE IF EXISTS temp.repair_fts_vocab;");
        }

        var ftsCorrupt = IsFtsCorrupt(connection);
        var pathsDrifted = Scalar(
            connection,
            "SELECT count(*) FROM fact f JOIN entity e ON e.id = f.subject_id WHERE f.path <> e.path;");

        // Foreign keys cascade salience deletes, so an orphan can only exist in a store
        // something else manipulated with enforcement off — which is exactly the kind of
        // store repair gets pointed at.
        var orphanSalience = Scalar(
            connection,
            "SELECT count(*) FROM salience WHERE fact_id NOT IN (SELECT id FROM fact);");

        var walPath = home.DatabasePath + "-wal";
        var walBytes = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;

        var report = new RepairReport(
            Applied: false,
            liveFacts,
            ftsMissing,
            ftsExtra,
            ftsCorrupt,
            FtsRebuilt: false,
            pathsDrifted,
            orphanSalience,
            walBytes,
            Vacuumed: false,
            SnapshotName: null);

        if (!apply)
        {
            return report;
        }

        var snapshot = BackupStore.Take(connection, home, now, label: "pre-repair");

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            if (pathsDrifted > 0)
            {
                using var fix = connection.CreateCommand();
                fix.Transaction = transaction;
                fix.CommandText =
                    """
                    UPDATE fact
                    SET path = (SELECT e.path FROM entity e WHERE e.id = fact.subject_id)
                    WHERE path <> (SELECT e.path FROM entity e WHERE e.id = fact.subject_id);
                    """;
                fix.ExecuteNonQuery();
            }

            if (orphanSalience > 0)
            {
                using var purge = connection.CreateCommand();
                purge.Transaction = transaction;
                purge.CommandText = "DELETE FROM salience WHERE fact_id NOT IN (SELECT id FROM fact);";
                purge.ExecuteNonQuery();
            }

            if (report.FtsNeedsRebuild)
            {
                EngramDatabase.RebuildFactFts(connection, transaction);
            }

            transaction.Commit();
        }

        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, "VACUUM;");

        return report with
        {
            Applied = true,
            FtsRebuilt = report.FtsNeedsRebuild,
            Vacuumed = true,
            SnapshotName = Path.GetFileName(snapshot.Path),
        };
    }

    private static bool IsFtsCorrupt(SqliteConnection connection)
    {
        try
        {
            Execute(connection, "INSERT INTO fact_fts(fact_fts, rank) VALUES('integrity-check', 0);");
            return false;
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (int)(long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
