using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

public class RepoCommandTests
{
    /// <summary>
    /// auto_index_on_session_start answers "may Engram index on its own", not "must Engram obey
    /// an instruction" — an explicit `repo enroll` is a command, not ambient work, so the setting
    /// that suppresses the session-start scan must not silence this too (spec §6.9, guard 16).
    /// Falsify by restoring --auto on the enrollment job: the index never runs, "1 file(s) indexed"
    /// never appears, and the promise at RepoCommand.cs's enroll message becomes a lie.
    /// </summary>
    [Fact]
    public void Enroll_IndexesEvenWithAutoIndexOnSessionStartDisabled()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var configPath = Path.Combine(home.Root, "config.toml");
        var config = File.ReadAllText(configPath);
        Assert.Contains("auto_index_on_session_start = true", config, StringComparison.Ordinal);
        File.WriteAllText(configPath, config.Replace(
            "auto_index_on_session_start = true", "auto_index_on_session_start = false"));

        var repo = Path.Combine(home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Program.cs"), "class Program {}\n");
        if (!GitInit(repo))
        {
            return;
        }

        var (enrollExit, enrollOut, _) = EngramProcess.Run(home.Root, "repo", "enroll", repo);
        Assert.Equal(0, enrollExit);
        Assert.Contains("first index is running in the background", enrollOut, StringComparison.Ordinal);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var listOutput = string.Empty;
        while (!listOutput.Contains("file(s) indexed", StringComparison.Ordinal) || listOutput.Contains("0 file(s) indexed", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(200);
            (_, listOutput, _) = EngramProcess.Run(home.Root, "repo", "list");
        }

        Assert.Contains("1 file(s) indexed", listOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// D49's load-bearing half (spec §4.2/§7): <c>engram repo index --all</c> without <c>--apply</c>
    /// must not create the store, migrate it, or otherwise touch anything under the home — a dry
    /// run that leaves a mark is not a dry run. Enrolls a repo, waits for its first (applied) index
    /// to finish, then forces it due again directly against <c>engram.db</c> so the dry run pass
    /// actually walks a due candidate rather than an empty list: <c>IndexTelemetry.Note</c> fires
    /// per candidate regardless of apply unless it is itself gated on it (spec §4.3), and a run
    /// with nothing due could never have caught that gate missing.
    /// </summary>
    [Fact]
    public void IndexAll_DryRun_WritesNothingIntoTheHome()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var repo = Path.Combine(home.Root, "checkout");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Program.cs"), "class Program {}\n");
        if (!GitInit(repo))
        {
            return;
        }

        var (enrollExit, enrollOut, _) = EngramProcess.Run(home.Root, "repo", "enroll", repo);
        Assert.Equal(0, enrollExit);
        Assert.Contains("first index is running in the background", enrollOut, StringComparison.Ordinal);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var listOutput = string.Empty;
        while (!listOutput.Contains("file(s) indexed", StringComparison.Ordinal) || listOutput.Contains("0 file(s) indexed", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(200);
            (_, listOutput, _) = EngramProcess.Run(home.Root, "repo", "list");
        }

        Assert.Contains("1 file(s) indexed", listOutput, StringComparison.Ordinal);

        var databasePath = Path.Combine(home.Root, "engram.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE repo_enrollment SET last_full_scan_at = NULL;";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var before = Snapshot(home.Root);
        var (exitCode, _, _) = EngramProcess.Run(home.Root, "repo", "index", "--all");
        var after = Snapshot(home.Root);

        Assert.Equal(0, exitCode);

        var moved = before.Keys.Union(after.Keys)
            .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
            .Select(path => $"{path}: {before.GetValueOrDefault(path) ?? "(absent)"} -> {after.GetValueOrDefault(path) ?? "(absent)"}")
            .ToList();

        Assert.True(moved.Count == 0, "a dry run changed the home it read:\n  " + string.Join("\n  ", moved));
    }

    /// <summary>
    /// spec §4.1/§7: <c>--all</c> is required, and a bare <c>engram repo index</c> is a usage error
    /// (exit 2) whose message still names the verb — the file's other usage strings were updated at
    /// the same time (spec §4.1) so a subcommand that exists is always one a user can find.
    /// </summary>
    [Fact]
    public void RepoIndexWithoutTheAllFlag_ExitsTwoAndNamesIndexInTheError()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "repo", "index");

        Assert.Equal(2, exitCode);
        Assert.Contains("index", stderr, StringComparison.Ordinal);
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
}
