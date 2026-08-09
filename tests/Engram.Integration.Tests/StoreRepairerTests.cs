using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// D8's boundary, exercised from both sides: everything derived is rebuilt, nothing
/// authored moves. Every plant asserts the breakage is real before repair claims to fix
/// it — a repair test whose store was never actually broken proves only that repair does
/// no harm to a healthy store, which is the cheap half of the contract.
/// </summary>
public class StoreRepairerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FtsDesync_IsDetected_AndRebuildRestoresSearch()
    {
        using var sandbox = new SandboxHome();
        long factId;

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = FactStore.Remember(
                connection,
                new FactWrite(
                    "/people/jim/preferences", "preference", "prefers",
                    "the zanzibar workflow for deploys", "user", "stated"),
                T0).FactId;
            FactStore.Remember(
                connection,
                new FactWrite(
                    "/projects/acme/notes", "note", "states",
                    "an unrelated engineering note", "project", "observed"),
                T0);

            // External-content FTS accepts a 'delete' for a row that still lives: the
            // exact desync a torn write or a foreign tool leaves behind.
            var live = FactStore.ReadById(connection, factId)!;
            using var desync = connection.CreateCommand();
            desync.CommandText =
                "INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path) "
                + "VALUES('delete', $id, $body, $predicate, $path);";
            desync.Parameters.AddWithValue("$id", factId);
            desync.Parameters.AddWithValue("$body", live.Body);
            desync.Parameters.AddWithValue("$predicate", live.Predicate);
            desync.Parameters.AddWithValue("$path", live.SubjectPath);
            desync.ExecuteNonQuery();

            Assert.DoesNotContain(FactStore.Search(connection, "zanzibar", 10), f => f.Id == factId);
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var dry = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0);
            Assert.False(dry.Applied);
            Assert.Equal(1, dry.FtsMissing);
            Assert.True(dry.FtsNeedsRebuild);

            var applied = StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0);
            Assert.True(applied.FtsRebuilt);

            Assert.Contains(FactStore.Search(connection, "zanzibar", 10), f => f.Id == factId);
            Assert.Equal(0, StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0).FtsMissing);
        }
    }

    [Fact]
    public void PathDrift_IsRederivedFromTheEntity_WithoutAnFtsRebuild()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = FactStore.Remember(
            connection,
            new FactWrite(
                "/projects/acme/decisions", "decision", "decided",
                "quarterly releases ship from trunk", "project", "stated"),
            T0).FactId;

        using (var drift = connection.CreateCommand())
        {
            drift.CommandText = "UPDATE fact SET path = '/wrong/spelling' WHERE id = $id;";
            drift.Parameters.AddWithValue("$id", factId);
            drift.ExecuteNonQuery();
        }

        Assert.Equal("/wrong/spelling", FactPath(connection, factId));

        var dry = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0);
        Assert.Equal(1, dry.PathsDrifted);

        var applied = StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0);
        Assert.Equal(1, applied.PathsDrifted);

        // The repath trigger kept the index in step on both the drift and the fix, so
        // the path repair alone must not have escalated to a rebuild.
        Assert.False(applied.FtsRebuilt);
        Assert.Equal("/projects/acme/decisions", FactPath(connection, factId));
        Assert.Equal(0, StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0).PathsDrifted);
    }

    [Fact]
    public void OrphanSalience_PlantedWithForeignKeysOff_IsDeleted()
    {
        using var sandbox = new SandboxHome();

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            FactStore.Remember(
                connection,
                new FactWrite("/people/jim", "person", "states", "a fact to anchor the store", "user", "stated"),
                T0);
        }

        // The provider sends foreign_keys=1 on every raw connection, so the orphan can
        // only be planted the way a foreign writer would create it: enforcement off.
        // Pooling off, or disposing returns the handle to the pool instead of closing it
        // and Windows then refuses to delete the sandbox with engram.db still open.
        using (var raw = new SqliteConnection($"Data Source={sandbox.Home.DatabasePath};Pooling=False"))
        {
            raw.Open();
            using var plant = raw.CreateCommand();
            plant.CommandText = "PRAGMA foreign_keys = 0; INSERT INTO salience(fact_id) VALUES (987654);";
            plant.ExecuteNonQuery();
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM salience WHERE fact_id = 987654;"));

            var dry = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0);
            Assert.Equal(1, dry.OrphanSalience);

            StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0);
            Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM salience WHERE fact_id = 987654;"));
        }
    }

    [Fact]
    public void Apply_SnapshotsFirst_AndNeverMovesAuthoredTruth()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var first = FactStore.Remember(
            connection,
            new FactWrite("/people/jim", "person", "states", "the original belief", "user", "stated"),
            T0).FactId;
        FactStore.Remember(
            connection,
            new FactWrite("/people/jim/preferences", "preference", "prefers", "terse reports", "user", "stated"),
            T0);
        FactStore.Forget(connection, first, "test retraction", T0.AddMinutes(1));

        using (var drift = connection.CreateCommand())
        {
            drift.CommandText = "UPDATE fact SET path = '/somewhere/else' WHERE id = $id;";
            drift.Parameters.AddWithValue("$id", first);
            drift.ExecuteNonQuery();
        }

        var before = BackupFingerprint.Read(connection);
        var backupsBefore = BackupStore.List(sandbox.Home).Count;

        var report = StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0.AddMinutes(2));

        Assert.Equal(before, BackupFingerprint.Read(connection));
        Assert.Equal(backupsBefore + 1, BackupStore.List(sandbox.Home).Count);
        Assert.NotNull(report.SnapshotName);
        Assert.Contains(BackupStore.List(sandbox.Home), s => s.Name == report.SnapshotName);
    }

    [Fact]
    public void DryRun_FixesNothing_AndTakesNoSnapshot()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = FactStore.Remember(
            connection,
            new FactWrite("/projects/acme/notes", "note", "states", "left drifting on purpose", "project", "observed"),
            T0).FactId;

        using (var drift = connection.CreateCommand())
        {
            drift.CommandText = "UPDATE fact SET path = '/still/wrong' WHERE id = $id;";
            drift.Parameters.AddWithValue("$id", factId);
            drift.ExecuteNonQuery();
        }

        var dry = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0);

        Assert.Equal(1, dry.PathsDrifted);
        Assert.Null(dry.SnapshotName);
        Assert.Empty(BackupStore.List(sandbox.Home));
        Assert.Equal("/still/wrong", FactPath(connection, factId));
        Assert.Equal(1, StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0).PathsDrifted);
    }

    [Fact]
    public void TokenIndexStale_IsDetected_AndApplyRebuildsIt()
    {
        using var sandbox = new SandboxHome();
        long factId;

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = FactStore.Remember(
                connection,
                new FactWrite(
                    "/projects/acme/notes", "note", "states",
                    "the zanzibar workflow ships releases", "project", "stated"),
                T0).FactId;

            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE schema_meta SET value = '0' WHERE key = 'fact_token_version';";
            stale.ExecuteNonQuery();
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var dry = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0);
            Assert.False(dry.Applied);
            Assert.True(dry.TokenIndexNeedsRebuild);
            Assert.False(FactTokenIndex.IsReady(connection));

            var applied = StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0);
            Assert.True(applied.TokenIndexRebuilt);
            Assert.True(FactTokenIndex.IsReady(connection));
            Assert.False(StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0).TokenIndexNeedsRebuild);
        }
    }

    [Fact]
    public void TokenMissing_IsDetected_AndApplyRepairsIt()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = FactStore.Remember(
            connection,
            new FactWrite(
                "/projects/acme/notes", "note", "states",
                "the zanzibar workflow ships releases", "project", "stated"),
            T0).FactId;

        Assert.Equal(0, FactTokenIndex.CountMissing(connection));

        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DELETE FROM fact_token WHERE fact_id = $id;";
            drop.Parameters.AddWithValue("$id", factId);
            drop.ExecuteNonQuery();
        }

        Assert.Equal(1, FactTokenIndex.CountMissing(connection));

        var dry = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0);
        Assert.Equal(1, dry.TokenMissing);
        Assert.True(dry.TokenIndexNeedsRebuild);

        var applied = StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0.AddMinutes(1));
        Assert.True(applied.TokenIndexRebuilt);
        Assert.Equal(0, FactTokenIndex.CountMissing(connection));
        Assert.Equal(0, StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0).TokenMissing);
    }

    [Fact]
    public void TokenExtra_IsDetected_AndApplyRepairsIt()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = FactStore.Remember(
            connection,
            new FactWrite(
                "/projects/acme/notes", "note", "states",
                "the trunk workflow ships releases", "project", "stated"),
            T0).FactId;
        FactStore.Forget(connection, factId, "no longer needed", T0.AddMinutes(1));

        // Forget already removed this fact's own rows through FactStore's chokepoint — plant
        // the extra row back by hand, the shape a foreign write or a missed Remove call leaves.
        using (var plant = connection.CreateCommand())
        {
            plant.CommandText = "INSERT INTO fact_token(token, fact_id) VALUES ('ghost', $id);";
            plant.Parameters.AddWithValue("$id", factId);
            plant.ExecuteNonQuery();
        }

        Assert.Equal(1, FactTokenIndex.CountExtra(connection));

        var dry = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0.AddMinutes(2));
        Assert.Equal(1, dry.TokenExtra);
        Assert.True(dry.TokenIndexNeedsRebuild);

        var applied = StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0.AddMinutes(2));
        Assert.True(applied.TokenIndexRebuilt);
        Assert.Equal(0, FactTokenIndex.CountExtra(connection));
        Assert.Equal(0, StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0).TokenExtra);
    }

    /// <summary>
    /// A live fact whose subject and body tokenize to nothing indexable has no row in
    /// <c>fact_token</c> and is supposed to have none, so it must not read as a missed
    /// <see cref="FactTokenIndex.Add"/>. The exclusion is load-bearing in one direction only:
    /// counting such a fact would leave <see cref="RepairReport.TokenIndexNeedsRebuild"/>
    /// permanently true, and a rebuild that cannot make its own trigger go away is one that
    /// runs forever. So the assertion that matters is the one after the apply.
    /// </summary>
    [Fact]
    public void AFactThatTokenizesToNothing_IsNotCountedMissing()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        // Every token here is either under three characters or a stopword, so IsIndexable
        // rejects all of them and Add correctly writes no rows at all.
        var factId = FactStore.Remember(
            connection,
            new FactWrite("/projects/acme/it", "it", "states", "as it is", "project", "stated"),
            T0).FactId;

        Assert.Equal(0, CountTokenRows(connection, factId));
        Assert.Equal(0, FactTokenIndex.CountMissing(connection));
        Assert.False(StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0).TokenIndexNeedsRebuild);

        // A rebuild does not invent rows for it either, so the detector stays quiet afterwards
        // rather than re-arming on the next run.
        StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0.AddMinutes(1));

        Assert.Equal(0, CountTokenRows(connection, factId));
        Assert.Equal(0, FactTokenIndex.CountMissing(connection));
        Assert.False(
            StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0.AddMinutes(2)).TokenIndexNeedsRebuild);
    }

    private static int CountTokenRows(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM fact_token WHERE fact_id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return (int)(long)command.ExecuteScalar()!;
    }

    [Fact]
    public void RepairApply_WithTokens_IsANoOpWhenTheIndexIsAlreadyCurrent()
    {
        using var sandbox = new SandboxHome();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "repair", "--apply", "--tokens"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("tokens: in sync", stdout.ToString());
        Assert.Empty(BackupStore.List(sandbox.Home));
    }

    /// <summary>
    /// Plants token staleness alongside an independent FTS desync and proves <c>--tokens</c>
    /// fixes only the former: no snapshot (nothing in the full apply pipeline ran at all), and
    /// the FTS desync — which a full <c>--apply</c> would have caught — is still there afterward.
    /// </summary>
    [Fact]
    public void RepairApply_WithTokens_RebuildsOnlyTheTokenIndex()
    {
        using var sandbox = new SandboxHome();
        long factId;

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = FactStore.Remember(
                connection,
                new FactWrite(
                    "/projects/acme/notes", "note", "states",
                    "the zanzibar workflow ships releases", "project", "stated"),
                T0).FactId;

            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE schema_meta SET value = '0' WHERE key = 'fact_token_version';";
            stale.ExecuteNonQuery();

            var live = FactStore.ReadById(connection, factId)!;
            using var desync = connection.CreateCommand();
            desync.CommandText =
                "INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path) "
                + "VALUES('delete', $id, $body, $predicate, $path);";
            desync.Parameters.AddWithValue("$id", factId);
            desync.Parameters.AddWithValue("$body", live.Body);
            desync.Parameters.AddWithValue("$predicate", live.Predicate);
            desync.Parameters.AddWithValue("$path", live.SubjectPath);
            desync.ExecuteNonQuery();
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "repair", "--apply", "--tokens"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("tokens: unbuilt or stale", stdout.ToString());
        Assert.Empty(BackupStore.List(sandbox.Home));

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.True(FactTokenIndex.IsReady(reopened));

        var stillDesynced = StoreRepairer.Repair(reopened, sandbox.Home, apply: false, T0.AddMinutes(1));
        Assert.Equal(1, stillDesynced.FtsMissing);
    }

    /// <summary>
    /// <c>--tokens</c> reads the readiness stamp and nothing else, so a row-level desync — the
    /// shape a missed <c>Add</c> leaves — goes unnoticed here on purpose. This runs from the
    /// session-start maintenance child on every session, and <c>CountMissing</c>/<c>CountExtra</c>
    /// scan the whole token table; putting that on this path is the cost this mode exists to
    /// avoid. The second half is the half that makes this a guard rather than a test of an
    /// omission: the full <c>repair</c> verb still sees the same desync, so detection was moved
    /// rather than deleted.
    /// </summary>
    [Fact]
    public void RepairApply_WithTokens_ReadsTheStampOnly_AndLeavesRowLevelDesyncToFullRepair()
    {
        using var sandbox = new SandboxHome();
        long factId;

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = FactStore.Remember(
                connection,
                new FactWrite(
                    "/projects/acme/notes", "note", "states",
                    "the zanzibar workflow ships releases", "project", "stated"),
                T0).FactId;

            // The stamp stays current; only the rows go, which is what a forgotten call site
            // leaves behind.
            using var drop = connection.CreateCommand();
            drop.CommandText = "DELETE FROM fact_token WHERE fact_id = $id;";
            drop.Parameters.AddWithValue("$id", factId);
            drop.ExecuteNonQuery();
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "repair", "--apply", "--tokens"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("tokens: in sync", stdout.ToString());

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(1, FactTokenIndex.CountMissing(reopened));
        Assert.True(StoreRepairer.Repair(reopened, sandbox.Home, apply: false, T0.AddMinutes(1)).TokenIndexNeedsRebuild);
    }

    [Fact]
    public void RepairTokens_WithoutApply_ReportsWithoutBuilding()
    {
        using var sandbox = new SandboxHome();

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE schema_meta SET value = '0' WHERE key = 'fact_token_version';";
            stale.ExecuteNonQuery();
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "repair", "--tokens"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry run only", stdout.ToString());

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.False(FactTokenIndex.IsReady(reopened));
    }

    [Fact]
    public void Command_WithoutAStore_IsARealError_ThatNamesInit()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "repair"], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("engram init", stderr.ToString());
        Assert.False(File.Exists(sandbox.Home.DatabasePath));
    }

    [Fact]
    public void Command_DryRunByDefault_SaysSo()
    {
        using var sandbox = new SandboxHome();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "repair"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry run only", stdout.ToString());
        Assert.Empty(BackupStore.List(sandbox.Home));
    }

    private static string FactPath(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return (string)command.ExecuteScalar()!;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
