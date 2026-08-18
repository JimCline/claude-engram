using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Cross-machine sync (docs/gp-adoption/01-sync-spec.md) between two real stores sharing a real
/// chunk directory on disk — the shape of a two-machine round trip, not a mock of one.
/// </summary>
public sealed class SyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static string NewSyncRoot() =>
        Path.Combine(Path.GetTempPath(), "engram-sync-" + Guid.NewGuid().ToString("N"));

    private static long Write(SqliteConnection connection, string path, string body, DateTimeOffset at) =>
        FactStore.Remember(connection, new FactWrite(path, "note", "states", body, "project", "stated"), at).FactId;

    private static long CountLive(SqliteConnection connection, string predicate = "states")
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fact WHERE predicate = $p AND valid_to IS NULL;";
        command.Parameters.AddWithValue("$p", predicate);
        return (long)command.ExecuteScalar()!;
    }

    private static string? BodyOf(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.body FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.valid_to IS NULL;
            """;
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Regression: <c>ResolveMachineId</c> must be resolved against each machine's own home-local
    /// <c>&lt;home&gt;/sync</c> (spec: "a small file at &lt;home&gt;/sync/machine-id"), never against
    /// the configurable, externally-shared exchange directory (<c>sync.dir</c>). Two real machines
    /// pointing <c>dir</c> at the very same shared location (e.g. a git repo both clone) still have
    /// distinct homes and must get distinct ids; resolving against the shared directory instead — as
    /// an earlier version of the CLI wiring did — makes whichever machine writes first "claim" the id
    /// for every machine that later reads that directory, silently merging two machines into one.
    /// Routes through <see cref="SyncCommand.Run"/> rather than calling <c>Sync.ResolveMachineId</c>
    /// directly: the bug this guards lived in <c>SyncCommand</c>'s own machine-id resolution line
    /// (which config knob it read), so a test that bypasses <c>SyncCommand</c> would pass unchanged
    /// even with that line reverted to the buggy <c>settings.ResolveDir(home)</c> form.
    /// </summary>
    [Fact]
    public void SyncExport_ThroughSyncCommand_NeverCollidesEvenWhenConfiguredDirIsShared()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: true);
            using var b = new SandboxHome(initialize: true);

            // Both machines configure the same externally-shared exchange directory — the
            // realistic "shared git repo / synced folder" deployment the spec describes.
            WriteSharedDirConfig(a.Home, syncRoot);
            WriteSharedDirConfig(b.Home, syncRoot);

            var stdoutA = new StringWriter();
            var stdoutB = new StringWriter();
            // --apply is what creates <home>/sync/machine-id (ResolveMachineId's creating branch);
            // machine-id is what the pre-fix bug (resolving against settings.ResolveDir(home)
            // instead of home.SyncDir) would have collided on.
            Assert.Equal(0, SyncCommand.Run(a.Home.Root, ["export", "--apply"], stdoutA, new StringWriter()));
            Assert.Equal(0, SyncCommand.Run(b.Home.Root, ["export", "--apply"], stdoutB, new StringWriter()));

            var machineA = File.ReadAllText(Path.Combine(a.Home.SyncDir, "machine-id"));
            var machineB = File.ReadAllText(Path.Combine(b.Home.SyncDir, "machine-id"));

            Assert.NotEqual(machineA, machineB);
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    private static void WriteSharedDirConfig(EngramHome home, string sharedDir)
    {
        File.AppendAllText(home.ConfigPath, $"\n[sync]\ndir = \"{sharedDir.Replace("\\", "\\\\")}\"\n");
    }

    [Fact]
    public void ExportThenImport_RoundTripsTheFactSet()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            Write(connA, "/project/x", "the first thing", T0);
            Write(connA, "/project/y", "the second thing", T0.AddMinutes(1));

            var machineA = Sync.ResolveMachineId(syncRoot);
            var exportResult = Sync.Export(connA, syncRoot, machineA, apply: true);
            Assert.Equal(2, exportResult.FactCount);

            var importResult = Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(2), apply: true, CloseResolver.DefaultRetryCeiling);

            Assert.Equal(2, importResult.Written);
            Assert.Equal(1, importResult.ChunksApplied);
            Assert.Equal("the first thing", BodyOf(connB, "/project/x"));
            Assert.Equal("the second thing", BodyOf(connB, "/project/y"));
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
    public void RevisingOnA_ThenSyncing_ClosesBsReplicatedCopy()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            Write(connA, "/project/x", "the old belief", T0);
            var machineA = Sync.ResolveMachineId(syncRoot);
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(1), apply: true, CloseResolver.DefaultRetryCeiling);
            Assert.Equal(1, CountLive(connB));

            // Revise on A: closes the old belief, opens a new one.
            Write(connA, "/project/x", "the new belief", T0.AddMinutes(2));
            Sync.Export(connA, syncRoot, machineA, apply: true);

            var importResult = Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(3), apply: true, CloseResolver.DefaultRetryCeiling);

            Assert.Equal("the new belief", BodyOf(connB, "/project/x"));
            Assert.Equal(1, CountLive(connB));
            Assert.True(importResult.Written >= 1);
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// B authors its own fact in the same slot before importing A's close for it — a genuine
    /// independent divergence, which D8 forbids a close from touching. The close is left
    /// untouched and counted, not silently applied.
    /// </summary>
    [Fact]
    public void AnIndependentlyAuthoredConflict_IsLeftUntouchedAndCounted()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            Write(connA, "/project/x", "green", T0);
            var machineA = Sync.ResolveMachineId(syncRoot);
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(1), apply: true, CloseResolver.DefaultRetryCeiling);

            // B revises independently — B now believes something A does not know about.
            Write(connB, "/project/x", "blue", T0.AddMinutes(2));

            // A closes its own (now-stale, from B's perspective) belief and re-exports.
            Write(connA, "/project/x", "green, still", T0.AddMinutes(3));
            Sync.Export(connA, syncRoot, machineA, apply: true);

            var importResult = Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(4), apply: true, CloseResolver.DefaultRetryCeiling);

            Assert.Equal("blue", BodyOf(connB, "/project/x"));
            Assert.True(importResult.Conflicted >= 1);

            var status = Sync.Status(connB, syncRoot, "b-machine");
            Assert.True(status.ConflictCount >= 1);
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Falsification for the conflict test above: with the live-exact-match branch collapsed to
    /// always-Apply (mirroring <c>CloseResolverTests.Falsification_...</c> at the store level), B's
    /// independently-authored fact would be silently closed by A's close record. This test asserts
    /// against exactly that outcome, so it fails red if <see cref="CloseResolver.Resolve"/> stops
    /// distinguishing case 2 from case 4.
    /// </summary>
    [Fact]
    public void Falsification_ConflictedRowMustNotHaveBeenClosedByA()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            Write(connA, "/project/x", "green", T0);
            var machineA = Sync.ResolveMachineId(syncRoot);
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(1), apply: true, CloseResolver.DefaultRetryCeiling);

            Write(connB, "/project/x", "blue", T0.AddMinutes(2));
            Write(connA, "/project/x", "green, still", T0.AddMinutes(3));
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(4), apply: true, CloseResolver.DefaultRetryCeiling);

            // If B's live fact is still "blue" and still live, the conflict was correctly left
            // alone. A defect that always-applies would leave "blue" closed here instead.
            using var command = connB.CreateCommand();
            command.CommandText =
                """
                SELECT body, valid_to IS NULL FROM fact f JOIN entity e ON e.id = f.subject_id
                WHERE e.path = '/project/x' AND f.body = 'blue';
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("blue", reader.GetString(0));
            Assert.True(reader.GetInt64(1) != 0, "B's independently authored fact must still be live");
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Regression for the pass-1/Replay ordering defect: when a predecessor is already live on B
    /// from an <em>earlier</em> import and its successor arrives in the <em>same</em> import batch as
    /// the close that names it, pass 1 applies the close (clearing the slot) before the successor
    /// exists locally, so its own successor lookup misses. Liveness and <c>valid_to</c> alone (as
    /// <see cref="RevisingOnA_ThenSyncing_ClosesBsReplicatedCopy"/> checks) come out right either way —
    /// only reading back <c>superseded_by</c> distinguishes a correctly linked close from one whose
    /// pointer was silently left null.
    /// </summary>
    [Fact]
    public void RevisingOnA_ThenSyncing_LinksTheClosedFactToItsReplacement()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            Write(connA, "/project/x", "the old belief", T0);
            var machineA = Sync.ResolveMachineId(syncRoot);
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(1), apply: true, CloseResolver.DefaultRetryCeiling);

            // Same import batch carries both the close for the old belief (already live on B from
            // the import above) and its successor — the exact scenario pass 1 resolves before
            // Replay has inserted the successor.
            Write(connA, "/project/x", "the new belief", T0.AddMinutes(2));
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(3), apply: true, CloseResolver.DefaultRetryCeiling);

            var successorId = FactId(connB, "/project/x", "the new belief", liveOnly: true);
            var predecessorSupersededBy = SupersededByOf(connB, "/project/x", "the old belief");

            Assert.NotNull(successorId);
            Assert.Equal(successorId, predecessorSupersededBy);
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    private static long? FactId(SqliteConnection connection, string path, string body, bool liveOnly)
    {
        using var command = connection.CreateCommand();
        command.CommandText = liveOnly
            ? """
              SELECT f.id FROM fact f JOIN entity e ON e.id = f.subject_id
              WHERE e.path = $path AND f.body = $body AND f.valid_to IS NULL;
              """
            : """
              SELECT f.id FROM fact f JOIN entity e ON e.id = f.subject_id
              WHERE e.path = $path AND f.body = $body;
              """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$body", body);
        return command.ExecuteScalar() is long id ? id : null;
    }

    private static long? SupersededByOf(SqliteConnection connection, string path, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.superseded_by FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.body = $body AND f.valid_to IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$body", body);
        return command.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>
    /// Both side tables (<c>sync_chunk_state</c>, <c>sync_deferred_close</c>) are derived, weakly,
    /// like the FTS index (D8): dropping them and re-running import over the same chunk history
    /// must reach the same resulting fact set, because nothing about them is authored truth.
    /// </summary>
    [Fact]
    public void DroppingBothSideTables_AndReimporting_RebuildsTheSameFactSet()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            Write(connA, "/project/x", "the old belief", T0);
            var machineA = Sync.ResolveMachineId(syncRoot);
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Write(connA, "/project/x", "the new belief", T0.AddMinutes(1));
            Sync.Export(connA, syncRoot, machineA, apply: true);

            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(2), apply: true, CloseResolver.DefaultRetryCeiling);
            var before = BodyOf(connB, "/project/x");
            var liveBefore = CountLive(connB);

            using (var drop = connB.CreateCommand())
            {
                drop.CommandText = "DELETE FROM sync_chunk_state; DELETE FROM sync_deferred_close;";
                drop.ExecuteNonQuery();
            }

            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(3), apply: true, CloseResolver.DefaultRetryCeiling);

            Assert.Equal(before, BodyOf(connB, "/project/x"));
            Assert.Equal(liveBefore, CountLive(connB));
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
