namespace Engram.EndToEnd.Tests;

/// <summary>
/// Drives <c>engram activity</c> through the published binary. Tier 3 rather than tier 2 because
/// the "never opens the database" claim is about what the binary does to a directory, not about
/// what a method returns — the same reasoning <c>DoctorCommandTests</c> uses for its own
/// file-snapshot test.
/// </summary>
public class ActivityCommandTests
{
    [Fact]
    public void Activity_AgainstASeededTelemetryFile_ExitsZeroReportsTheLastEventAndNeverOpensTheDatabase()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var now = DateTimeOffset.UtcNow;
        File.WriteAllLines(Path.Combine(home.Root, "telemetry.jsonl"), new[]
        {
            $$"""{"timestamp":"{{now.AddMinutes(-1).ToString("o")}}","session_id":"s1","kind":"recall"}""",
            $$"""{"timestamp":"{{now.ToString("o")}}","session_id":"s1","kind":"digest"}""",
        });

        var before = Snapshot(home.Root);
        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "activity");
        var after = Snapshot(home.Root);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("last: digest", stdout, StringComparison.Ordinal);

        // init (run by the TestHome fixture) already creates engram.db, so absence isn't the
        // signal — the snapshot diff below is: activity must not touch anything init left behind,
        // engram.db included.
        var moved = before.Keys.Union(after.Keys)
            .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
            .ToList();
        Assert.True(moved.Count == 0, "activity changed the home it read:\n  " + string.Join("\n  ", moved));
    }

    /// <summary>
    /// The snapshot test above catches writes, not an open — reading a file and closing it again
    /// changes neither its size nor its mtime, so it cannot tell "never opened" from "opened and
    /// read cleanly." Revoking read permission on <c>engram.db</c> can: an accidental
    /// <c>EngramDatabase.OpenInitialized</c> call fails to open the file and the process would
    /// exit non-zero or print nothing, where a genuinely telemetry-only read is unaffected.
    /// </summary>
    [Fact]
    public void Activity_WithAnUnreadableDatabase_StillSucceeds_ProvingItNeverOpensIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX file permissions only");

        using var home = new TestHome();

        var now = DateTimeOffset.UtcNow;
        File.WriteAllLines(Path.Combine(home.Root, "telemetry.jsonl"), new[]
        {
            $$"""{"timestamp":"{{now.ToString("o")}}","session_id":"s1","kind":"digest"}""",
        });

        var databasePath = Path.Combine(home.Root, "engram.db");
        Assert.True(File.Exists(databasePath), "precondition: TestHome's init created a database");

        // Written as a guard rather than relying on the skip above, so the platform analyzer can
        // see that these calls are unreachable on Windows.
        if (!OperatingSystem.IsWindows())
        {
            var originalMode = File.GetUnixFileMode(databasePath);
            File.SetUnixFileMode(databasePath, UnixFileMode.None);
            try
            {
                var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "activity");

                Assert.Equal(0, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Contains("last: digest", stdout, StringComparison.Ordinal);
            }
            finally
            {
                // Restored so TestHome can delete itself.
                File.SetUnixFileMode(databasePath, originalMode);
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
