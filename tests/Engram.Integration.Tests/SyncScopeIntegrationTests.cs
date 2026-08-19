using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Scoped export (docs/memory-expansion/01-sync-spec.md, "Scoped export") against real stores:
/// repo-path matching across code-indexed and session-tied facts, the per-fact always-sync flag,
/// its propagation through engram_revise, and close-selection's independence from scope.
/// </summary>
public class SyncScopeIntegrationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static string NewSyncRoot() =>
        Path.Combine(Path.GetTempPath(), "engram-sync-scope-" + Guid.NewGuid().ToString("N"));

    private static long Write(
        SqliteConnection connection, string path, string body, DateTimeOffset at, string scope, long? sessionId = null) =>
        FactStore.Remember(connection, new FactWrite(path, "note", "states", body, scope, "stated", SessionId: sessionId), at).FactId;

    private static void RegisterRepo(SqliteConnection connection, string repoPath, string identity, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES ($path, $identity, $disk, $now);";
        command.Parameters.AddWithValue("$path", repoPath);
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$disk", repoPath);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    private static long OpenSessionUnderRepo(SqliteConnection connection, string externalId, string repoPath, DateTimeOffset now)
    {
        var sessionId = SessionStore.EnsureSession(connection, null, externalId, now);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE session SET repo_path = $repoPath WHERE id = $id;";
        command.Parameters.AddWithValue("$repoPath", repoPath);
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();

        return sessionId;
    }

    private static bool FactExists(SqliteConnection connection, string path, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1 FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.body = $body AND f.valid_to IS NULL;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$body", body);
        return command.ExecuteScalar() is not null;
    }

    private static long LiveFactId(SqliteConnection connection, string path, string predicate)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.predicate = $predicate AND f.valid_to IS NULL;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$predicate", predicate);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void RepoScopedExport_CarriesRepoTiedAndFlaggedFactsOnly_NotPlainUserFacts()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            RegisterRepo(connA, "/repos/target", "target-repo", T0);

            Write(connA, "/repos/target/src/Foo.cs", "the code fact", T0, scope: "code");

            var sessionId = OpenSessionUnderRepo(connA, "session-a", "/repos/target", T0);
            Write(connA, "/session-note/1", "the session fact", T0.AddMinutes(1), scope: "session", sessionId: sessionId);

            Write(connA, "/prefs/color", "the plain fact", T0.AddMinutes(2), scope: "user");

            var flaggedId = Write(connA, "/other/thing", "the flagged fact", T0.AddMinutes(3), scope: "user");
            FactSyncRequests.Insert(connA, null, flaggedId, T0.AddMinutes(3).ToUnixTimeSeconds());

            var machineA = Sync.ResolveMachineId(syncRoot);
            var exportResult = Sync.Export(connA, syncRoot, machineA, apply: true, scope: "repo:target-repo");
            Assert.Null(exportResult.Error);
            Assert.Equal(3, exportResult.FactCount);

            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(5), apply: true, CloseResolver.DefaultRetryCeiling);

            Assert.True(FactExists(connB, "/repos/target/src/Foo.cs", "the code fact"));
            Assert.True(FactExists(connB, "/session-note/1", "the session fact"));
            Assert.True(FactExists(connB, "/other/thing", "the flagged fact"));
            Assert.False(FactExists(connB, "/prefs/color", "the plain fact"));
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Revise_PropagatesAnExistingSyncFlagToTheNewFact_WithoutASecondExplicitFlag()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = Write(connection, "/prefs/color", "green", T0, scope: "user");
        FactSyncRequests.Insert(connection, null, factId, T0.ToUnixTimeSeconds());

        var session = new McpSessionId("session-a");
        var handle = FactCatalog.HandleFor(factId);

        EngramMcpTools.Revise(sandbox.Home, session, new McpHomeState(true), handle, "blue", "corrected");

        var revisedId = LiveFactId(connection, "/prefs/color", "states");
        Assert.NotEqual(factId, revisedId);
        Assert.True(FactSyncRequests.IsFlagged(connection, null, revisedId));
    }

    [Fact]
    public void ScopeNarrowingAfterExport_StillTransmitsACloseForAnAlreadyExportedFact()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            Write(connA, "/prefs/color", "green", T0, scope: "user");

            var machineA = Sync.ResolveMachineId(syncRoot);
            var exportResult = Sync.Export(connA, syncRoot, machineA, apply: true, scope: SyncScope.Default);
            Assert.Equal(1, exportResult.FactCount);

            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(1), apply: true, CloseResolver.DefaultRetryCeiling);
            Assert.True(FactExists(connB, "/prefs/color", "green"));

            RegisterRepo(connA, "/repos/unrelated", "unrelated-repo", T0.AddMinutes(2));

            FactStore.Remember(
                connA,
                new FactWrite("/prefs/color", "note", "states", "blue", "user", "stated"),
                T0.AddMinutes(3));

            var narrowedExport = Sync.Export(connA, syncRoot, machineA, apply: true, scope: "repo:unrelated-repo");
            Assert.Null(narrowedExport.Error);

            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(4), apply: true, CloseResolver.DefaultRetryCeiling);

            Assert.False(FactExists(connB, "/prefs/color", "green"));
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }
}
