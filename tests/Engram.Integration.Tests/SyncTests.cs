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

    /// <summary>
    /// Falsifies fork 14's resolution (spec, "[sync] enabled gates MaintenanceLauncher's automatic
    /// invocation"): only <see cref="MaintenanceLauncher"/>'s ambient session-start script is gated
    /// by <c>[sync] enabled</c>. <see cref="SyncCommand"/>'s Export/Import/Compact handlers carry no
    /// <c>settings.Enabled</c> check of their own, so a command a person typed by hand still runs —
    /// the same precedent <c>repo enroll</c> already sets against <c>auto_index_on_session_start</c>.
    /// Falsify: add a <c>settings.Enabled</c> check into any handler and this test starts failing.
    /// </summary>
    [Fact]
    public void SyncEnabled_DoesNotGateAnExplicitlyInvokedExport()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var home = new SandboxHome(initialize: false);
            using (var connection = EngramDatabase.OpenInitialized(home.Home))
            {
                Write(connection, "/project/x", "explicit export body", T0);
            }

            // [sync] enabled is never written here, so it stays at its documented default
            // (SyncSettings.DefaultEnabled = false).
            WriteSharedDirConfig(home.Home, syncRoot);

            var stdout = new StringWriter();
            Assert.Equal(0, SyncCommand.Run(home.Home.Root, ["export", "--apply"], stdout, new StringWriter()));

            Assert.True(
                Directory.Exists(home.Home.SyncDir),
                "an explicit `sync export --apply` must write <home>/sync even with [sync] enabled left at its false default");
            var machineId = File.ReadAllText(Path.Combine(home.Home.SyncDir, "machine-id"));
            var chunkPath = Path.Combine(syncRoot, machineId, "1.jsonl");
            Assert.True(File.Exists(chunkPath), $"expected a chunk at {chunkPath}");
            Assert.Contains("explicit export body", File.ReadAllText(chunkPath), StringComparison.Ordinal);
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
    /// The disabled-state note is advisory only (spec, resolved Open question 14) — it must not
    /// change <c>sync status</c>'s exit code or suppress its other reporting.
    /// </summary>
    [Fact]
    public void SyncStatus_WithSyncDisabled_StillReportsAndAddsTheDisabledNote()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var home = new SandboxHome(initialize: true);
            WriteSharedDirConfig(home.Home, syncRoot);

            var stdout = new StringWriter();
            Assert.Equal(0, SyncCommand.Run(home.Home.Root, ["status"], stdout, new StringWriter()));

            var output = stdout.ToString();
            Assert.Contains("[sync] enabled = false", output, StringComparison.Ordinal);
            Assert.Contains("This machine:", output, StringComparison.Ordinal);
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

    /// <summary>
    /// End-to-end staleness (docs/memory-expansion/01-sync-spec.md, "Staleness/liveness
    /// detection"): a peer just imported reads Ok, and the same peer reads Warn once this
    /// machine's own knowledge of it — both <c>sync_chunk_state.applied_at</c> and the chunk
    /// file's own mtime — is older than <c>stale_after_days</c>. Backdates those two inputs
    /// directly rather than sleeping the test; both feed <see cref="Sync.GatherPeerObservations"/>,
    /// which <see cref="Diagnostics"/>' sync check compares against the real
    /// <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    [Fact]
    public void Staleness_APeerReadsOkThenWarnOncePastTheThreshold()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            File.AppendAllText(
                b.Home.ConfigPath,
                $"\n[sync]\nenabled = true\ndir = \"{syncRoot.Replace("\\", "\\\\")}\"\nstale_after_days = 14\n");

            Write(connA, "/project/x", "the first thing", T0);
            var machineA = Sync.ResolveMachineId(syncRoot);
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Sync.Import(connB, syncRoot, "b-machine", DateTimeOffset.UtcNow, apply: true, CloseResolver.DefaultRetryCeiling);

            var freshReport = Diagnostics.Run(b.Home, _ => null, reachOut: false);
            var freshSync = freshReport.Checks.Single(c => c.Name == "sync");
            Assert.Equal(DiagnosisState.Ok, freshSync.State);

            var backdated = DateTimeOffset.UtcNow.AddDays(-15);
            using (var update = connB.CreateCommand())
            {
                update.CommandText = "UPDATE sync_chunk_state SET applied_at = $at WHERE machine_id = $m;";
                update.Parameters.AddWithValue("$at", backdated.ToUnixTimeSeconds());
                update.Parameters.AddWithValue("$m", machineA);
                update.ExecuteNonQuery();
            }

            var chunkFile = Directory.EnumerateFiles(Path.Combine(syncRoot, machineA), "*.jsonl").Single();
            File.SetLastWriteTimeUtc(chunkFile, backdated.UtcDateTime);

            var staleReport = Diagnostics.Run(b.Home, _ => null, reachOut: false);
            var staleSync = staleReport.Checks.Single(c => c.Name == "sync");
            Assert.Equal(DiagnosisState.Warn, staleSync.State);
            Assert.Contains(machineA, staleSync.Detail, StringComparison.Ordinal);
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
    /// Falsification for the "never observed is never stale" half of the same rule, exercised
    /// end-to-end: a freshly enrolled peer directory with no chunks and no
    /// <c>sync_chunk_state</c> row yet must read Ok, never Warn — there is nothing to compare
    /// against.
    /// </summary>
    [Fact]
    public void Staleness_AFreshlyEnrolledPeerWithNoChunksYet_IsNotStale()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var b = new SandboxHome(initialize: false);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            File.AppendAllText(
                b.Home.ConfigPath,
                $"\n[sync]\nenabled = true\ndir = \"{syncRoot.Replace("\\", "\\\\")}\"\nstale_after_days = 14\n");

            Directory.CreateDirectory(Path.Combine(syncRoot, "brand-new-peer"));

            var report = Diagnostics.Run(b.Home, _ => null, reachOut: false);
            var sync = report.Checks.Single(c => c.Name == "sync");
            Assert.Equal(DiagnosisState.Ok, sync.State);
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
    /// The core hazard <c>sync compact</c> exists to handle safely (docs/memory-expansion/
    /// 01-sync-spec.md, "Chunk retention/pruning"): A closes one fact well inside
    /// <c>retain_days</c> and another well past it, B fully imports before A ever compacts, then
    /// A compacts and a brand-new machine C imports only the post-compaction chunk. C's live set
    /// must match B's, and C must never receive the fact A dropped — while B, which already
    /// caught up, keeps it forever, since compact only ever rewrites A's own outgoing history.
    /// </summary>
    [Fact]
    public void Compact_ALiveFactSurvives_AndDroppedClosedHistoryNeverReachesALateJoiningPeer()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var c = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);
            using var connC = EngramDatabase.OpenInitialized(c.Home);

            var machineA = Sync.ResolveMachineId(syncRoot);

            Write(connA, "/project/live", "live body", T0);
            Write(connA, "/project/recent-closed", "recent-closed v1", T0);
            Write(connA, "/project/old-closed", "old-closed v1", T0);
            Sync.Export(connA, syncRoot, machineA, apply: true);

            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(1), apply: true, CloseResolver.DefaultRetryCeiling);

            // Closed well inside retain_days (90d), relative to the compact `now` below.
            Write(connA, "/project/old-closed", "old-closed v2", T0.AddDays(5));
            Write(connA, "/project/recent-closed", "recent-closed v2", T0.AddDays(89));
            Sync.Export(connA, syncRoot, machineA, apply: true);

            var beforeCompact = Sync.Compact(syncRoot, machineA, apply: false, TimeSpan.FromDays(90), T0.AddDays(100));
            Assert.Equal(2, beforeCompact.ChunkFilesBefore);

            var compacted = Sync.Compact(syncRoot, machineA, apply: true, TimeSpan.FromDays(90), T0.AddDays(100));
            Assert.Equal(1, compacted.ChunkFilesAfter);
            Assert.Contains(compacted.Dropped, i => i.Body == "old-closed v1");
            Assert.DoesNotContain(compacted.Dropped, i => i.Body == "recent-closed v1");

            Sync.Import(connC, syncRoot, "c-machine", T0.AddDays(101), apply: true, CloseResolver.DefaultRetryCeiling);

            // B, which already caught up before compaction, keeps the dropped history forever —
            // compact only ever rewrites A's own outgoing directory.
            Assert.NotNull(FactId(connB, "/project/old-closed", "old-closed v1", liveOnly: false));

            // C, joining only after compaction, never receives it at all.
            Assert.Null(FactId(connC, "/project/old-closed", "old-closed v1", liveOnly: false));

            // C's live set matches what actually survived compaction.
            Assert.Equal("live body", BodyOf(connC, "/project/live"));
            Assert.Equal("old-closed v2", BodyOf(connC, "/project/old-closed"));
            Assert.Equal("recent-closed v2", BodyOf(connC, "/project/recent-closed"));
            Assert.Equal(3, CountLive(connC));

            // And the retained-but-closed identity's history is intact on C too.
            Assert.NotNull(FactId(connC, "/project/recent-closed", "recent-closed v1", liveOnly: false));
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
    /// The idempotent-replay hazard (D32) applied to compaction: B imports a fact while it is
    /// still open, then A closes it and compacts before B ever sees the close (the chunk that
    /// would have carried it standalone gets deleted along with everything else A rewrites). B's
    /// later import must still resolve the close correctly against its own already-live copy —
    /// the consolidated chunk carries the fact's original open-form line byte-faithfully (so it
    /// resolves as already-present rather than a fresh insert) alongside the close record.
    /// </summary>
    [Fact]
    public void Compact_APeerMidFlightOnAnAsYetUnseenClose_StillResolvesItCorrectlyAfterCompaction()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var b = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);
            using var connB = EngramDatabase.OpenInitialized(b.Home);

            var machineA = Sync.ResolveMachineId(syncRoot);

            Write(connA, "/project/y", "v1 body", T0);
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Sync.Import(connB, syncRoot, "b-machine", T0.AddMinutes(1), apply: true, CloseResolver.DefaultRetryCeiling);
            Assert.Equal("v1 body", BodyOf(connB, "/project/y"));

            // A closes v1 and exports the close — B has not imported this chunk yet.
            Write(connA, "/project/y", "v2 body", T0.AddDays(10));
            Sync.Export(connA, syncRoot, machineA, apply: true);

            // A compacts before B ever fetches that chunk: both of A's chunks are deleted and
            // replaced by one consolidated chunk B has never seen either.
            Sync.Compact(syncRoot, machineA, apply: true, TimeSpan.FromDays(90), T0.AddDays(20));

            var result = Sync.Import(connB, syncRoot, "b-machine", T0.AddDays(21), apply: true, CloseResolver.DefaultRetryCeiling);

            Assert.Equal(0, result.Conflicted);
            Assert.Equal("v2 body", BodyOf(connB, "/project/y"));
            Assert.Equal(1, CountLive(connB));

            var successorId = FactId(connB, "/project/y", "v2 body", liveOnly: true);
            var predecessorSupersededBy = SupersededByOf(connB, "/project/y", "v1 body");
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

    /// <summary>
    /// The load-bearing invariant the spec calls out explicitly: <c>sync compact</c> rewrites and
    /// deletes only files under this machine's own <c>&lt;syncRoot&gt;/&lt;machineId&gt;/</c> —
    /// never a peer's. Folder-sync transports propagate a peer-directory deletion back to that
    /// peer in near-real-time, unlike git, so a violation here is not cosmetic.
    /// </summary>
    [Fact]
    public void Compact_NeverTouchesAnyPathOutsideItsOwnMachineDirectory()
    {
        var syncRoot = NewSyncRoot();
        try
        {
            using var a = new SandboxHome(initialize: false);
            using var connA = EngramDatabase.OpenInitialized(a.Home);

            var machineA = Sync.ResolveMachineId(syncRoot);
            Write(connA, "/project/x", "v1", T0);
            Sync.Export(connA, syncRoot, machineA, apply: true);
            Write(connA, "/project/x", "v2", T0.AddDays(200));
            Sync.Export(connA, syncRoot, machineA, apply: true);

            var peerDir = Path.Combine(syncRoot, "peer-machine");
            Directory.CreateDirectory(peerDir);
            var peerFile = Path.Combine(peerDir, "1.jsonl");
            File.WriteAllText(peerFile, "{\"t\":\"fact\"}\n");

            var before = SnapshotOutside(syncRoot, machineA);

            Sync.Compact(syncRoot, machineA, apply: true, TimeSpan.FromDays(90), T0.AddDays(300));

            var after = SnapshotOutside(syncRoot, machineA);

            Assert.Equal(before, after);
            Assert.True(File.Exists(peerFile));
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    private static Dictionary<string, (long Size, DateTime Mtime)> SnapshotOutside(string syncRoot, string excludeMachineId)
    {
        var snapshot = new Dictionary<string, (long, DateTime)>();
        foreach (var file in Directory.EnumerateFiles(syncRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(syncRoot, file);
            if (relative.StartsWith(excludeMachineId + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(file);
            snapshot[relative] = (info.Length, info.LastWriteTimeUtc);
        }

        return snapshot;
    }
}
