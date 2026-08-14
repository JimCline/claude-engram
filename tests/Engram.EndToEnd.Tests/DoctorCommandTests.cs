using System.Text.Json;
using System.Threading;

using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Drives <c>engram doctor</c> through the published binary.
/// </summary>
/// <remarks>
/// <para>Tier 3 rather than tier 2 for two reasons the JIT build cannot reach.
/// <see cref="DoctorJson_IsWellFormedFromTheAotBinary"/> exercises a source-generated
/// <c>JsonSerializerContext</c>, which is exactly the shape that works under reflection and
/// throws once trimmed — a passing integration test proves nothing about it. And
/// <see cref="Doctor_WritesNothingIntoTheHomeItReads"/> can only be honest against a real process
/// with a real <c>ENGRAM_HOME</c>, since the read-only claim is about what the binary does to a
/// directory, not about what a method returns.</para>
/// </remarks>
public class DoctorCommandTests
{
    private static readonly string[] ExpectedChecks =
        ["home", "store", "server", "claude code", "embedding", "vector index", "backups", "edit queue", "code analysis", "tree-sitter"];

    [Fact]
    public void Doctor_OnAnInitialisedHome_ExitsZeroAndReportsEveryCheck()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "doctor", "--offline", "--no-repo");

        Assert.True(exitCode == 0, $"doctor failed on a freshly initialised home:\n{stdout}\n{stderr}");
        Assert.Equal(string.Empty, stderr);

        foreach (var check in ExpectedChecks)
        {
            Assert.Contains(check, stdout, StringComparison.Ordinal);
        }

        Assert.Contains(home.Root, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorJson_IsWellFormedFromTheAotBinary()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "doctor", "--json", "--offline", "--no-repo");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        Assert.True(root.GetProperty("healthy").GetBoolean());
        Assert.Equal(home.Root, root.GetProperty("home").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));

        var names = root.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString())
            .ToList();

        foreach (var check in ExpectedChecks)
        {
            Assert.Contains(check, names);
        }

        // Every row carries a state the renderer knows how to label; an unmapped one would print
        // as BROKEN and quietly overstate the diagnosis.
        foreach (var check in root.GetProperty("checks").EnumerateArray())
        {
            Assert.Contains(check.GetProperty("state").GetString(), (string[])["ok", "off", "warn", "broken"]);
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("detail").GetString()));
        }
    }

    /// <summary>
    /// The claim the whole design rests on, tested where it can actually be false.
    /// </summary>
    /// <remarks>
    /// Opening the store with <c>OpenInitialized</c> rather than <c>Open</c> would migrate an
    /// out-of-date schema and, per D31, snapshot it first — so this fails on a new file in
    /// <c>backups/</c> long before anyone notices the version moved.
    /// </remarks>
    [Fact]
    public void Doctor_WritesNothingIntoTheHomeItReads()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var before = Snapshot(home.Root);
        var (exitCode, _, _) = EngramProcess.Run(home.Root, "doctor", "--offline", "--no-repo");
        var after = Snapshot(home.Root);

        Assert.Equal(0, exitCode);

        var moved = before.Keys.Union(after.Keys)
            .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
            .Select(path => $"{path}: {before.GetValueOrDefault(path) ?? "(absent)"} -> {after.GetValueOrDefault(path) ?? "(absent)"}")
            .ToList();

        Assert.True(moved.Count == 0, "doctor changed the home it read:\n  " + string.Join("\n  ", moved));
    }

    [Fact]
    public void Doctor_OnAHomeThatWasNeverInitialised_ExitsOneAndSaysToRunInit()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        var root = Path.Combine(Path.GetTempPath(), "engram-e2e-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (exitCode, stdout, _) = EngramProcess.Run(root, "doctor", "--offline", "--no-repo");

            Assert.Equal(1, exitCode);
            Assert.Contains("BROKEN", stdout, StringComparison.Ordinal);
            Assert.Contains("engram init", stdout, StringComparison.Ordinal);

            // The same verdict through the other renderer. Asserted because the exit code is
            // computed once per output path, so one of them can drift to always-zero and still
            // print a report full of problems.
            var (jsonExit, jsonOut, _) = EngramProcess.Run(root, "doctor", "--json", "--offline", "--no-repo");

            Assert.Equal(1, jsonExit);
            using var document = JsonDocument.Parse(jsonOut);
            Assert.False(document.RootElement.GetProperty("healthy").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Doctor_WithAnUnknownFlag_ExitsTwoRatherThanReportingHealth()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "doctor", "--not-a-flag");

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--not-a-flag", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// docs/repo-index-remediation-spec.md §7: doctor against a home with a suppressed repo
    /// exits 0 and prints the warning. DiagnosticsTests.cs covers the same condition at the
    /// object level asserting <c>DiagnosisState</c>; only the rendered text here can prove the
    /// warning actually reaches doctor's stdout.
    /// </summary>
    [Fact]
    public void Doctor_OnARepoWithSuppressedDeletions_ExitsZeroAndPrintsTheWarning()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var repo = Path.Combine(home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A;\n");
        if (!GitInit(repo))
        {
            return;
        }

        EnrollAndWaitForFirstIndex(home.Root, repo);

        // The next full scan finds nothing against an already-indexed repo, which suppresses
        // its deletions rather than trusting an empty listing (commit E2).
        File.Delete(Path.Combine(repo, "a.cs"));

        var (indexExit, _, indexErr) = EngramProcess.Run(home.Root, "index", "--apply", "--full", repo);
        Assert.True(indexExit == 0, $"index exited {indexExit}: {indexErr}");

        var (doctorExit, doctorOut, doctorErr) = EngramProcess.RunWithStdinFromDirectory(
            home.Root, repo, stdin: null, "doctor", "--offline");

        Assert.True(doctorExit == 0, $"doctor exited {doctorExit}:\n{doctorOut}\n{doctorErr}");
        Assert.Contains(
            "found no files against an already-indexed repo, so its deletions were skipped",
            doctorOut,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// docs/repo-index-remediation-spec.md §7 and §14.5.2: <c>EngramDatabase.Open</c>, which
    /// doctor uses, never migrates (D31/D37), so a store between an upgrade and its next
    /// migration genuinely has no <c>last_scan_suppressed_reason</c> column — and that is
    /// routine, not a fault.
    /// </summary>
    [Fact]
    public void Doctor_OnAStoreMissingTheSuppressionColumn_ExitsZeroWithNoBrokenRow()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var repo = Path.Combine(home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A;\n");
        if (!GitInit(repo))
        {
            return;
        }

        EnrollAndWaitForFirstIndex(home.Root, repo);

        // Engram.EndToEnd.Tests deliberately has no reference to Engram.Core, so the v7 shape
        // is built with raw SQL rather than EngramDatabase/RepoEnrollment, mirroring what
        // SchemaMigrationTests.WriteVersion7Store does for the integration tier.
        var databasePath = Path.Combine(home.Root, "engram.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();

            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE repo_registry DROP COLUMN last_scan_suppressed_reason;";
            alter.ExecuteNonQuery();

            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "UPDATE schema_meta SET value = '7' WHERE key = 'schema_version';";
            downgrade.ExecuteNonQuery();
        }

        var (doctorExit, doctorOut, doctorErr) = EngramProcess.RunWithStdinFromDirectory(
            home.Root, repo, stdin: null, "doctor", "--offline");

        Assert.True(doctorExit == 0, $"doctor exited {doctorExit}:\n{doctorOut}\n{doctorErr}");
        Assert.DoesNotContain("BROKEN", doctorOut, StringComparison.Ordinal);
        Assert.Contains("suppression check not applicable", doctorOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// docs/repo-index-remediation-spec.md §14.5.2's second absence: a store that predates the
    /// repository index has no <c>repo_registry</c> table at all, not merely a missing column,
    /// and that is routine too — the same <c>EngramDatabase.Open</c>-never-migrates reasoning as
    /// <see cref="Doctor_OnAStoreMissingTheSuppressionColumn_ExitsZeroWithNoBrokenRow"/>. Built by
    /// dropping the table outright rather than by relying on a rolled-back fixture happening not
    /// to create it — the latter is D60's trap and would leave the guard proving nothing.
    /// </summary>
    [Fact]
    public void Doctor_OnAStoreMissingTheRepoRegistryTable_ExitsZeroWithNoBrokenRow()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var repo = Path.Combine(home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A;\n");
        if (!GitInit(repo))
        {
            return;
        }

        EnrollAndWaitForFirstIndex(home.Root, repo);

        var databasePath = Path.Combine(home.Root, "engram.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();

            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE IF EXISTS repo_registry;";
            drop.ExecuteNonQuery();
        }

        var (doctorExit, doctorOut, doctorErr) = EngramProcess.RunWithStdinFromDirectory(
            home.Root, repo, stdin: null, "doctor", "--offline");

        Assert.True(doctorExit == 0, $"doctor exited {doctorExit}:\n{doctorOut}\n{doctorErr}");
        Assert.DoesNotContain("BROKEN", doctorOut, StringComparison.Ordinal);
        Assert.Contains("repo check not applicable", doctorOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// Polls <c>repo list</c> until the first index that <c>repo enroll</c> spawns in the
    /// background finishes — that spawn has no synchronous or suppressible form, so the only way
    /// to observe completion from outside is watching the reported file count settle. Matches the
    /// pattern already established in RepoCommandTests.cs and ConcurrentIndexConvergenceTests.cs.
    /// </summary>
    private static void EnrollAndWaitForFirstIndex(string home, string repo)
    {
        var (enrollExit, enrollOut, enrollErr) = EngramProcess.Run(home, "repo", "enroll", repo);
        Assert.True(enrollExit == 0, $"repo enroll exited {enrollExit}: {enrollErr}");
        Assert.Contains("first index is running in the background", enrollOut, StringComparison.Ordinal);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var listOutput = string.Empty;
        while (!listOutput.Contains("file(s) indexed", StringComparison.Ordinal)
            || listOutput.Contains("0 file(s) indexed", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(200);
            (_, listOutput, _) = EngramProcess.Run(home, "repo", "list");
        }

        Assert.Contains("1 file(s) indexed", listOutput, StringComparison.Ordinal);
    }

    /// <summary><c>repo enroll</c> refuses a directory that isn't a git checkout.</summary>
    private static bool GitInit(string directory)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("init");
            info.ArgumentList.Add("-q");

            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Every file under a root, by path, size and last write — enough to catch a rewrite.</summary>
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
