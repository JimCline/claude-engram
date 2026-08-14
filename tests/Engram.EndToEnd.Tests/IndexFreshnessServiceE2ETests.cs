namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3, Commit F (spec §7): the background freshness service against the published binary.
/// </summary>
public class IndexFreshnessServiceE2ETests
{
    /// <summary>
    /// The sibling to <see cref="RepoCommandTests.IndexAll_DryRun_WritesNothingIntoTheHome"/> that
    /// test's own doc comment calls for: the same dry-run-writes-nothing scenario, but with a
    /// server running throughout. <c>auto_index_in_background</c> is off by default, so the running
    /// service is expected to have declined already (spec §6.6) — this proves a live server does
    /// not turn a dry run into a write, not that the background loop itself did anything.
    /// </summary>
    [Fact]
    public void IndexAll_DryRun_WithServerRunning_WritesNothingIntoTheHome()
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
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE repo_enrollment SET last_full_scan_at = NULL;";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var port = FreeTcpPort.Next();
        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        try
        {
            var before = Snapshot(home.Root);
            var (exitCode, _, _) = EngramProcess.Run(home.Root, "repo", "index", "--all");
            var after = Snapshot(home.Root);

            Assert.Equal(0, exitCode);

            var moved = before.Keys.Union(after.Keys)
                .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
                .Select(path => $"{path}: {before.GetValueOrDefault(path) ?? "(absent)"} -> {after.GetValueOrDefault(path) ?? "(absent)"}")
                .ToList();

            Assert.True(moved.Count == 0, "a dry run changed the home it read while a server was running:\n  " + string.Join("\n  ", moved));
        }
        finally
        {
            var (stopExit, _, stopErr) = EngramProcess.Run(home.Root, "stop");
            Assert.True(stopExit == 0, $"stop failed: {stopErr}");
        }
    }

    /// <summary>
    /// D42/D54's ownership test, driven end to end: <c>indexing.json</c> is written once the
    /// background loop actually ticks and is cleared by <c>ApplicationStopping</c>, the same way
    /// <c>embedding.json</c> is (spec §6.8). A stale note claiming to be current after a clean
    /// <c>engram stop</c> is exactly what a reader outside the process cannot tell apart from a
    /// server that is still up.
    /// </summary>
    [Fact]
    public void Stop_AfterBackgroundIndexingRan_LeavesNoIndexingNote()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var configPath = Path.Combine(home.Root, "config.toml");
        var config = File.ReadAllText(configPath);
        Assert.Contains("auto_index_in_background = false", config, StringComparison.Ordinal);
        File.WriteAllText(configPath, config.Replace(
            "auto_index_in_background = false", "auto_index_in_background = true"));

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

        var port = FreeTcpPort.Next();
        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"start failed: {startErr}");

        var indexProgressPath = Path.Combine(home.Root, "indexing.json");

        try
        {
            var wroteNote = false;
            var noteDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < noteDeadline)
            {
                if (File.Exists(indexProgressPath))
                {
                    wroteNote = true;
                    break;
                }

                Thread.Sleep(200);
            }

            Assert.True(wroteNote, "the background freshness service never wrote indexing.json");
        }
        finally
        {
            var (stopExit, _, stopErr) = EngramProcess.Run(home.Root, "stop");
            Assert.True(stopExit == 0, $"stop failed: {stopErr}");
        }

        Assert.False(File.Exists(indexProgressPath), "indexing.json survived a clean stop");
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
