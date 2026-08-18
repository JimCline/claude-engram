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
