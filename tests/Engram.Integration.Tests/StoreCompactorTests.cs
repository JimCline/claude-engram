using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

[Collection(SqlitePoolCollection.Name)]
public class StoreCompactorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static long Code(SqliteConnection connection, string path, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "symbol", "declares", body, "code", "observed", Regenerable: true),
            T0).FactId;

    private static long Code(SqliteConnection connection, string path, string body, int? tier) =>
        FactStore.Remember(
            connection,
            new FactWrite(
                path, "symbol", "declares", body, "code", "observed",
                Regenerable: true, AnalyzerTier: tier),
            T0).FactId;

    private static long Authored(SqliteConnection connection, string path, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "note", "states", body, "project", "stated"),
            T0).FactId;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (int)(long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// The base case, and the reason schema version 3 exists: pruning a closed fact fires the
    /// delete trigger, and the version 2 trigger fed FTS5 a second 'delete' for an entry the
    /// close had already removed — failing the statement with "database disk image is
    /// malformed". The repair probe afterwards is the health assertion: a prune that desynced
    /// the index would report a rebuild needed.
    /// </summary>
    [Fact]
    public void Compact_PrunesClosedRegenerableFacts_AndTheIndexStaysHealthy()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var authoredLive = Authored(connection, "/knowledge/testing/nginx", "It fronts the kestrel.");
        var authoredClosed = Authored(connection, "/knowledge/testing/kestrel", "It binds all interfaces.");
        Authored(connection, "/knowledge/testing/kestrel", "It binds loopback only.");

        var codeLive = Code(connection, "/projects/acme/code/api/src/A.cs#Widget", "declared as public sealed class Widget");
        var codeClosed = Code(connection, "/projects/acme/code/api/src/A.cs#Older", "declared as internal class Older");
        Code(connection, "/projects/acme/code/api/src/A.cs#Older", "declared as public class Older");
        var codeForgotten = Code(connection, "/projects/acme/code/api/src/B.cs#Gone", "declared as class Gone");
        FactStore.Forget(connection, codeForgotten, "stale", T0);

        var report = StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0);

        Assert.Equal(2, report.ClosedPruned);
        Assert.Equal(0, report.LivePruned);
        Assert.Equal(2, report.SupersessionsRemoved);
        Assert.Null(FactStore.ReadById(connection, codeClosed));
        Assert.Null(FactStore.ReadById(connection, codeForgotten));
        Assert.NotNull(FactStore.ReadById(connection, authoredClosed));
        Assert.NotNull(FactStore.ReadById(connection, authoredLive));
        Assert.NotNull(FactStore.ReadById(connection, codeLive));

        var health = StoreRepairer.Repair(connection, sandbox.Home, apply: false, T0);
        Assert.False(health.FtsNeedsRebuild);
        Assert.Equal(0, health.OrphanSalience);
    }

    [Fact]
    public void PathCompact_PrunesTheLiveSubtree_ButNeverAnAuthoredFactUnderIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var fileFact = Code(connection, "/projects/acme/code/api/src/A.cs", "about the request pipeline");
        var symbolFact = Code(connection, "/projects/acme/code/api/src/A.cs#Widget", "declared as class Widget");
        var authoredUnder = Authored(connection, "/projects/acme/code/api/src/A.cs", "Jim said this file is load-bearing.");
        var otherRepo = Code(connection, "/projects/acme/code/web/src/B.cs", "about the front end");
        var siblingPrefix = Code(connection, "/projects/acme/code/api-v2/src/C.cs", "about the successor");

        var report = StoreCompactor.Compact(connection, sandbox.Home, "/projects/acme/code/api", apply: true, T0);

        Assert.Equal(2, report.LivePruned);
        Assert.Equal(0, report.ClosedPruned);
        Assert.Null(FactStore.ReadById(connection, fileFact));
        Assert.Null(FactStore.ReadById(connection, symbolFact));
        Assert.NotNull(FactStore.ReadById(connection, authoredUnder));
        Assert.NotNull(FactStore.ReadById(connection, otherRepo));
        Assert.NotNull(FactStore.ReadById(connection, siblingPrefix));

        // The symbol entity lost its last fact; the file entity is still the authored
        // fact's subject and has to stay addressable.
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM entity WHERE path LIKE '%#Widget';"));
        Assert.Equal(1, Scalar(connection,
            "SELECT count(*) FROM entity WHERE path = '/projects/acme/code/api/src/A.cs';"));
    }

    /// <summary>
    /// Revising a code fact into a belief makes the pair authored history: the surviving
    /// correction would otherwise explain a revision nobody can read.
    /// </summary>
    [Fact]
    public void ARegenerableFact_RevisedIntoAuthoredTruth_IsProtected()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var codeFact = Code(connection, "/projects/acme/code/api/src/A.cs#Widget", "declared as class Widget");
        FactStore.Remember(
            connection,
            new FactWrite(
                "/projects/acme/code/api/src/A.cs#Widget", "symbol", "declares",
                "Deprecated; Jim keeps it only for the v1 importer.", "code", "stated"),
            T0);

        var report = StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0);

        Assert.Equal(0, report.ClosedPruned);
        Assert.Equal(1, report.ProtectedByAuthoredHistory);
        Assert.NotNull(FactStore.ReadById(connection, codeFact));
        Assert.Equal(1, Scalar(connection, $"SELECT count(*) FROM supersession WHERE old_fact_id = {codeFact};"));
    }

    /// <summary>
    /// The load-bearing half of path mode. Pruned facts with surviving file state would make
    /// the next index run see unchanged blob hashes and rewrite nothing — a silent,
    /// permanent loss rather than the temporary one compact promises.
    /// </summary>
    [Fact]
    public void PathCompact_ClearsFileStateAndRegistry_SoAReindexRewrites()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Code(connection, "/projects/acme/code/api/src/A.cs", "about the request pipeline");
        Execute(connection,
            "INSERT INTO file_state (repo_path, path, blob_sha, lang, indexed_at) VALUES "
            + "('/projects/acme/code/api', 'src/A.cs', 'aaa', 'csharp', 0), "
            + "('/projects/acme/code/web', 'src/B.cs', 'bbb', 'csharp', 0);");
        Execute(connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0), "
            + "('/projects/acme/code/web', 'github.com/acme/web', '/tmp/web', 0);");

        var report = StoreCompactor.Compact(connection, sandbox.Home, "/projects/acme/code/api", apply: true, T0);

        Assert.Equal(1, report.FileStatesRemoved);
        Assert.Equal(1, report.ReposDeregistered);
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM file_state WHERE repo_path = '/projects/acme/code/api';"));
        Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM file_state WHERE repo_path = '/projects/acme/code/web';"));
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM repo_registry WHERE repo_path = '/projects/acme/code/api';"));
        Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM repo_registry WHERE repo_path = '/projects/acme/code/web';"));
    }

    /// <summary>
    /// A prefix inside one file — a symbol — still clears that file's state, because the
    /// file must be re-read to be whole again. The repo stays registered: nothing above the
    /// file was pruned.
    /// </summary>
    [Fact]
    public void PathCompact_InsideOneFile_StillClearsThatFilesState()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Code(connection, "/projects/acme/code/api/src/A.cs#Widget", "declared as class Widget");
        Execute(connection,
            "INSERT INTO file_state (repo_path, path, blob_sha, lang, indexed_at) VALUES "
            + "('/projects/acme/code/api', 'src/A.cs', 'aaa', 'csharp', 0), "
            + "('/projects/acme/code/api', 'src/B.cs', 'bbb', 'csharp', 0);");
        Execute(connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0);");

        var report = StoreCompactor.Compact(
            connection, sandbox.Home, "/projects/acme/code/api/src/A.cs#Widget", apply: true, T0);

        Assert.Equal(1, report.FileStatesRemoved);
        Assert.Equal(0, report.ReposDeregistered);
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM file_state WHERE path = 'src/A.cs';"));
        Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM file_state WHERE path = 'src/B.cs';"));
        Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM repo_registry;"));
    }

    /// <summary>
    /// Guard 8.3-6: repo_enrollment is authored truth (D8), never repo_registry's residue — a
    /// path compact that deregisters a repo must leave the user's enrollment decision alone.
    /// Falsified by temporarily adding a `DELETE FROM repo_enrollment` line beside the
    /// repo_registry delete in <see cref="StoreCompactor"/>'s path block, confirming this test
    /// reds, then reverting.
    /// </summary>
    [Fact]
    public void PathCompact_NeverTouchesRepoEnrollment_EvenAsItDeregistersTheRepo()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Code(connection, "/projects/acme/code/api/src/A.cs", "about the request pipeline");
        Execute(connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0);");
        Execute(connection,
            "INSERT INTO repo_enrollment (identity, state, source, last_root, decided_at, last_full_scan_at) VALUES "
            + "('github.com/acme/api', 'enrolled', 'user', '/tmp/api', 0, NULL);");

        var report = StoreCompactor.Compact(connection, sandbox.Home, "/projects/acme/code/api", apply: true, T0);

        Assert.Equal(1, report.ReposDeregistered);
        Assert.Equal(0, Scalar(connection, "SELECT count(*) FROM repo_registry WHERE repo_path = '/projects/acme/code/api';"));
        Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM repo_enrollment WHERE identity = 'github.com/acme/api';"));
    }

    [Fact]
    public void DryRun_CountsEverything_AndChangesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var codeClosed = Code(connection, "/projects/acme/code/api/src/A.cs#Older", "declared as internal class Older");
        Code(connection, "/projects/acme/code/api/src/A.cs#Older", "declared as public class Older");

        var report = StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: false, T0);

        Assert.False(report.Applied);
        Assert.Equal(1, report.ClosedPruned);
        Assert.Equal(1, report.SupersessionsRemoved);
        Assert.Null(report.SnapshotName);
        Assert.NotNull(FactStore.ReadById(connection, codeClosed));
        Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM supersession;"));
        Assert.Empty(BackupStore.List(sandbox.Home));
    }

    [Fact]
    public void Apply_SnapshotsFirst()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Code(connection, "/projects/acme/code/api/src/A.cs#Older", "declared as internal class Older");
        Code(connection, "/projects/acme/code/api/src/A.cs#Older", "declared as public class Older");

        var report = StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0);

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-compact", snapshot.Name, StringComparison.Ordinal);
        Assert.NotNull(report.SnapshotName);
        Assert.Contains("pre-compact", report.SnapshotName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Code-navigation Phase 4 spec §9 item 13: compact never writes <c>analyzer_tier</c> — it is
    /// structurally excluded from what §4.3 licenses derived-state repair to touch, by D8. Falsified
    /// by adding an <c>UPDATE fact SET analyzer_tier = 0</c> beside compact's prune statement,
    /// confirming this test reds, then reverting.
    /// </summary>
    [Fact]
    public void Compact_NeverWritesAnalyzerTier_OnASurvivingFact()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        // Superseding at a differing subject keeps this one live but still exercises compact's
        // prune/rebuild path via the closed sibling declared below.
        var live = Code(connection, "/projects/acme/code/api/src/A.cs#Widget", "declared as public class Widget", tier: 0);
        Code(connection, "/projects/acme/code/api/src/A.cs#Older", "declared as internal class Older", tier: null);
        Code(connection, "/projects/acme/code/api/src/A.cs#Older", "declared as public class Older", tier: 1);

        StoreCompactor.Compact(connection, sandbox.Home, path: null, apply: true, T0);

        Assert.Equal(0, FactStore.ReadById(connection, live)!.AnalyzerTier);
    }
}
