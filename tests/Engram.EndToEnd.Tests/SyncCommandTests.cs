using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Drives <c>engram sync export/import/status</c> through the published binary, between two real
/// homes sharing one chunk directory — the same two-machine shape as the tier-2 simulation, but
/// against the AOT binary's own JSON handling.
/// </summary>
public class SyncCommandTests
{
    /// <summary>
    /// The published (Native AOT) binary parses <c>sync</c>'s subcommands and its own JSON
    /// chunk format without a reflection-based serializer to fall back on — the same reason
    /// <see cref="DoctorCommandTests.DoctorJson_IsWellFormedFromTheAotBinary"/> is tier 3 rather
    /// than tier 2. The domain logic (close resolution, cross-chunk supersession, conflicts) is
    /// covered by <c>Engram.Integration.Tests.SyncTests</c> against real stores; this exercises
    /// the CLI surface end to end: a real export produces a chunk file the AOT binary itself can
    /// read back, and a real import against a second home lands the fact.
    /// </summary>
    [Fact]
    public void ExportThenImport_ThroughTheBinary_RoundTripsAFact()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var a = new TestHome();
        using var b = new TestHome();
        var syncRoot = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppendSyncDir(a.Root, syncRoot);
            AppendSyncDir(b.Root, syncRoot);

            var bundlePath = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-bundle-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllLines(bundlePath,
            [
                """{"format":"engram-facts","format_version":1,"schema_version":9,"written_at":"2026-08-17T00:00:00Z"}""",
                """{"id":1,"subject":"/project/x","kind":"note","predicate":"states","body":"the first thing","object":null,"object_kind":null,"scope":"project","learned_via":"stated","regenerable":false,"evidence":null,"valid_from":1755388800,"valid_to":null,"superseded_by":null,"reason":null,"created_at":1755388800,"details":null}""",
            ]);

            var (seedCode, _, seedErr) = EngramProcess.Run(a.Root, "import", bundlePath, "--apply");
            Assert.Equal(0, seedCode);

            var (exportCode, exportOut, exportErr) = EngramProcess.Run(a.Root, "sync", "export", "--apply");
            Assert.Equal(0, exportCode);
            Assert.Contains("fact(s)", exportOut, StringComparison.Ordinal);

            var (importCode, importOut, importErr) = EngramProcess.Run(b.Root, "sync", "import", "--apply");
            Assert.Equal(0, importCode);

            var (statusCode, statusOut, _) = EngramProcess.Run(b.Root, "sync", "status");
            Assert.Equal(0, statusCode);
            Assert.Contains("No pending chunks", statusOut, StringComparison.Ordinal);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(b.Root, "engram.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM fact f JOIN entity e ON e.id = f.subject_id
                WHERE e.path = '/project/x' AND f.body = 'the first thing' AND f.valid_to IS NULL;
                """;
            Assert.Equal(1L, (long)command.ExecuteScalar()!);

            File.Delete(bundlePath);
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
    /// Regression: <c>sync import --if-new</c> must stop being a permanent no-op after the first
    /// peer exchange. An earlier version's cheap check (<c>HasAnyPeerDirectory</c>) only asked
    /// whether a peer directory existed, so once any peer had ever exported, every later
    /// <c>--if-new</c> import opened the store and ran a full scan regardless of whether anything
    /// was new. The mtime/watermark check must correctly skip a second no-op run and correctly
    /// stop skipping once the peer has genuinely exported again.
    /// </summary>
    [Fact]
    public void ImportIfNew_SkipsASecondNoOpCheck_ButNotAfterThePeerExportsAgain()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var a = new TestHome();
        using var b = new TestHome();
        var syncRoot = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-ifnew-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppendSyncDir(a.Root, syncRoot);
            AppendSyncDir(b.Root, syncRoot);

            var bundlePath = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-ifnew-bundle-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllLines(bundlePath,
            [
                """{"format":"engram-facts","format_version":1,"schema_version":9,"written_at":"2026-08-17T00:00:00Z"}""",
                """{"id":1,"subject":"/project/x","kind":"note","predicate":"states","body":"the first thing","object":null,"object_kind":null,"scope":"project","learned_via":"stated","regenerable":false,"evidence":null,"valid_from":1755388800,"valid_to":null,"superseded_by":null,"reason":null,"created_at":1755388800,"details":null}""",
            ]);
            var (seedCode, _, _) = EngramProcess.Run(a.Root, "import", bundlePath, "--apply");
            Assert.Equal(0, seedCode);

            var (exportCode, _, _) = EngramProcess.Run(a.Root, "sync", "export", "--apply");
            Assert.Equal(0, exportCode);

            var (firstImportCode, _, _) = EngramProcess.Run(b.Root, "sync", "import", "--if-new", "--apply");
            Assert.Equal(0, firstImportCode);

            var (secondImportCode, secondImportOut, _) = EngramProcess.Run(b.Root, "sync", "import", "--if-new", "--apply");
            Assert.Equal(0, secondImportCode);
            Assert.Contains("Skipped", secondImportOut, StringComparison.Ordinal);

            var bundlePath2 = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-ifnew-bundle2-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllLines(bundlePath2,
            [
                """{"format":"engram-facts","format_version":1,"schema_version":9,"written_at":"2026-08-17T00:00:01Z"}""",
                """{"id":2,"subject":"/project/y","kind":"note","predicate":"states","body":"the second thing","object":null,"object_kind":null,"scope":"project","learned_via":"stated","regenerable":false,"evidence":null,"valid_from":1755388801,"valid_to":null,"superseded_by":null,"reason":null,"created_at":1755388801,"details":null}""",
            ]);
            var (seedCode2, _, _) = EngramProcess.Run(a.Root, "import", bundlePath2, "--apply");
            Assert.Equal(0, seedCode2);

            // A newer chunk mtime than B's watermark must make the third check due again.
            var (reExportCode, _, _) = EngramProcess.Run(a.Root, "sync", "export", "--apply");
            Assert.Equal(0, reExportCode);

            var (thirdImportCode, thirdImportOut, _) = EngramProcess.Run(b.Root, "sync", "import", "--if-new", "--apply");
            Assert.Equal(0, thirdImportCode);
            Assert.DoesNotContain("Skipped", thirdImportOut, StringComparison.Ordinal);

            File.Delete(bundlePath);
            File.Delete(bundlePath2);
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    private static void AppendSyncDir(string homeRoot, string syncRoot)
    {
        var configPath = Path.Combine(homeRoot, "config.toml");
        File.AppendAllText(configPath, $"\n[sync]\ndir = \"{syncRoot.Replace("\\", "\\\\")}\"\n");
    }

    /// <summary>
    /// <c>sync</c> must not write anywhere outside the sync directory and the store it is told to
    /// use — the same "doctor never leaves a mark" invariant, adapted (spec: no export/import may
    /// touch <c>file-touched</c>'s queue or any other unrelated home file).
    /// </summary>
    [Fact]
    public void Status_WritesNothingIntoTheHomeItReads()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var before = Snapshot(home.Root);
        var (exitCode, _, _) = EngramProcess.Run(home.Root, "sync", "status");
        var after = Snapshot(home.Root);

        Assert.Equal(0, exitCode);

        var moved = before.Keys.Union(after.Keys)
            .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
            .Select(path => $"{path}: {before.GetValueOrDefault(path) ?? "(absent)"} -> {after.GetValueOrDefault(path) ?? "(absent)"}")
            .ToList();

        Assert.True(moved.Count == 0, "sync status changed the home it read:\n  " + string.Join("\n  ", moved));
    }

    /// <summary>
    /// Regression: <c>sync export</c>/<c>sync import</c> without <c>--apply</c> must not create
    /// <c>&lt;home&gt;/sync/machine-id</c> — an earlier version called the id-creating
    /// <c>ResolveMachineId</c> unconditionally, so a dry run wrote to disk despite D49's "print
    /// what would happen, no write without --apply". Unlike <see cref="Status_WritesNothingIntoTheHomeItReads"/>,
    /// export/import legitimately write telemetry on every run including a dry one (D55/D56), so
    /// this checks specifically for the machine-id file rather than the whole home being untouched.
    /// </summary>
    [Fact]
    public void Export_WithoutApply_DoesNotCreateMachineId()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, _) = EngramProcess.Run(home.Root, "sync", "export");

        Assert.Equal(0, exitCode);
        Assert.False(
            File.Exists(Path.Combine(home.Root, "sync", "machine-id")),
            "sync export (dry run) created <home>/sync/machine-id");
    }

    /// <summary>Same invariant as <see cref="Export_WithoutApply_DoesNotCreateMachineId"/>, for import.</summary>
    [Fact]
    public void Import_WithoutApply_DoesNotCreateMachineId()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var syncRoot = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-dry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(syncRoot);

        try
        {
            AppendSyncDir(home.Root, syncRoot);

            var (exitCode, _, _) = EngramProcess.Run(home.Root, "sync", "import");

            Assert.Equal(0, exitCode);
            Assert.False(
                File.Exists(Path.Combine(home.Root, "sync", "machine-id")),
                "sync import (dry run) created <home>/sync/machine-id");
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
    /// docs/memory-expansion/01-sync-spec.md's Tier-3 line covers <c>compact</c> alongside
    /// export/import: a real export, a real compact against the AOT binary's own JSON chunk
    /// writer/reader, and a real import against a second home that has never seen the
    /// pre-compaction chunks at all — only the consolidated one. Seeds A with an
    /// already-closed-and-superseded pair in one bundle (the same shape a real backup/replay
    /// produces) so the single export already exercises the "exported already-closed" fact-line
    /// form compact must re-emit byte-faithfully.
    /// </summary>
    [Fact]
    public void ExportCompactThenImport_ThroughTheBinary_RoundTripsTheConsolidatedChunk()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var a = new TestHome();
        using var b = new TestHome();
        var syncRoot = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-compact-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppendSyncDir(a.Root, syncRoot);
            AppendSyncDir(b.Root, syncRoot);

            // sync compact's retention window is measured from real wall-clock time (D-unnamed, see
            // SyncCompaction), so v1's close has to stay recent relative to whenever this test
            // actually runs rather than a fixed calendar date the retain window would eventually
            // age past.
            var closeMoment = DateTimeOffset.UtcNow;
            var v1From = closeMoment.AddHours(-2).ToUnixTimeSeconds();
            var v1To = closeMoment.AddHours(-1).ToUnixTimeSeconds();
            var v2From = v1To;

            var bundlePath = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-compact-bundle-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllLines(bundlePath,
            [
                """{"format":"engram-facts","format_version":1,"schema_version":9,"written_at":"2026-08-17T00:00:00Z"}""",
                $$"""{"id":1,"subject":"/project/x","kind":"note","predicate":"states","body":"v1 body","object":null,"object_kind":null,"scope":"project","learned_via":"stated","regenerable":false,"evidence":null,"valid_from":{{v1From}},"valid_to":{{v1To}},"superseded_by":2,"reason":null,"created_at":{{v1From}},"details":null}""",
                $$"""{"id":2,"subject":"/project/x","kind":"note","predicate":"states","body":"v2 body","object":null,"object_kind":null,"scope":"project","learned_via":"stated","regenerable":false,"evidence":null,"valid_from":{{v2From}},"valid_to":null,"superseded_by":null,"reason":null,"created_at":{{v2From}},"details":null}""",
            ]);

            var (seedCode, _, seedErr) = EngramProcess.Run(a.Root, "import", bundlePath, "--apply");
            Assert.True(seedCode == 0, $"seed import exited {seedCode}: {seedErr}");

            var (exportCode, _, exportErr) = EngramProcess.Run(a.Root, "sync", "export", "--apply");
            Assert.True(exportCode == 0, $"sync export exited {exportCode}: {exportErr}");

            var (compactCode, compactOut, compactErr) = EngramProcess.Run(a.Root, "sync", "compact", "--apply");
            Assert.True(compactCode == 0, $"sync compact exited {compactCode}: {compactErr}");
            Assert.Contains("Chunk files", compactOut, StringComparison.Ordinal);

            var (importCode, _, importErr) = EngramProcess.Run(b.Root, "sync", "import", "--apply");
            Assert.True(importCode == 0, $"sync import exited {importCode}: {importErr}");

            var (statusCode, statusOut, _) = EngramProcess.Run(b.Root, "sync", "status");
            Assert.Equal(0, statusCode);
            Assert.Contains("No pending chunks", statusOut, StringComparison.Ordinal);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(b.Root, "engram.db")}");
            connection.Open();

            using (var live = connection.CreateCommand())
            {
                live.CommandText =
                    """
                    SELECT COUNT(*) FROM fact f JOIN entity e ON e.id = f.subject_id
                    WHERE e.path = '/project/x' AND f.body = 'v2 body' AND f.valid_to IS NULL;
                    """;
                Assert.Equal(1L, (long)live.ExecuteScalar()!);
            }

            using (var closed = connection.CreateCommand())
            {
                closed.CommandText =
                    """
                    SELECT f2.body FROM fact f1
                    JOIN entity e ON e.id = f1.subject_id
                    JOIN fact f2 ON f2.id = f1.superseded_by
                    WHERE e.path = '/project/x' AND f1.body = 'v1 body';
                    """;
                Assert.Equal("v2 body", (string)closed.ExecuteScalar()!);
            }

            File.Delete(bundlePath);
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
    /// docs/memory-expansion/01-sync-spec.md's Tier-3 line, the file-snapshot half: "nothing
    /// outside the sync directory and the target DB changes". <paramref name="syncRoot"/> already
    /// lives outside <c>home.Root</c> (see <see cref="AppendSyncDir"/>), so a snapshot of
    /// <c>home.Root</c> alone covers "outside the sync directory" by construction; the store's own
    /// files and telemetry (written by every sync verb on every run, D55/D56 — the same allowance
    /// <see cref="Export_WithoutApply_DoesNotCreateMachineId"/> documents) are excluded rather than
    /// asserting the whole home untouched, since compact is expected to touch neither.
    /// </summary>
    [Fact]
    public void Compact_WritesNothingOutsideTheSyncDirAndTheStore()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var syncRoot = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-compact-snap-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppendSyncDir(home.Root, syncRoot);

            var bundlePath = Path.Combine(Path.GetTempPath(), "engram-e2e-sync-compact-snap-bundle-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllLines(bundlePath,
            [
                """{"format":"engram-facts","format_version":1,"schema_version":9,"written_at":"2026-08-17T00:00:00Z"}""",
                """{"id":1,"subject":"/project/x","kind":"note","predicate":"states","body":"the first thing","object":null,"object_kind":null,"scope":"project","learned_via":"stated","regenerable":false,"evidence":null,"valid_from":1755388800,"valid_to":null,"superseded_by":null,"reason":null,"created_at":1755388800,"details":null}""",
            ]);
            var (seedCode, _, seedErr) = EngramProcess.Run(home.Root, "import", bundlePath, "--apply");
            Assert.True(seedCode == 0, $"seed import exited {seedCode}: {seedErr}");

            var (exportCode, _, exportErr) = EngramProcess.Run(home.Root, "sync", "export", "--apply");
            Assert.True(exportCode == 0, $"sync export exited {exportCode}: {exportErr}");

            var before = Snapshot(home.Root);
            var (compactCode, _, compactErr) = EngramProcess.Run(home.Root, "sync", "compact", "--apply");
            var after = Snapshot(home.Root);

            Assert.True(compactCode == 0, $"sync compact exited {compactCode}: {compactErr}");

            var excluded = new[] { "engram.db", "engram.db-wal", "engram.db-shm", "telemetry.jsonl" };
            var moved = before.Keys.Union(after.Keys)
                .Where(path => !excluded.Contains(path, StringComparer.Ordinal))
                .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
                .Select(path => $"{path}: {before.GetValueOrDefault(path) ?? "(absent)"} -> {after.GetValueOrDefault(path) ?? "(absent)"}")
                .ToList();

            Assert.True(moved.Count == 0, "sync compact changed something outside the sync dir and the store:\n  " + string.Join("\n  ", moved));

            File.Delete(bundlePath);
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, recursive: true);
            }
        }
    }

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (!Directory.Exists(root))
        {
            return files;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            files[Path.GetRelativePath(root, path)] = $"{info.Length}@{info.LastWriteTimeUtc:O}";
        }

        return files;
    }
}
