using Engram.Core;

namespace Engram.Integration.Tests;

public class SpoolReaderTests
{
    [Fact]
    public void Drain_ReturnsEntriesInChronologicalOrder_AndLeavesQueueDirectoryEmpty()
    {
        using var sandbox = new SandboxHome();
        var queueDir = sandbox.Home.QueueDir;
        Directory.CreateDirectory(queueDir);

        var baseTicks = DateTime.UtcNow.Ticks;
        WriteSpoolFile(queueDir, baseTicks, "1111aaaa", At(1), "/repo/first.cs");
        WriteSpoolFile(queueDir, baseTicks + 1, "2222bbbb", At(2), "/repo/second.cs");
        WriteSpoolFile(queueDir, baseTicks + 2, "3333cccc", At(3), "/repo/third.cs");

        var entries = SpoolReader.Drain(queueDir);

        Assert.Equal(
            ["/repo/first.cs", "/repo/second.cs", "/repo/third.cs"],
            entries.Select(entry => entry.Path));
        Assert.Equal([At(1), At(2), At(3)], entries.Select(entry => entry.At));
        Assert.Empty(Directory.GetFiles(queueDir));
    }

    /// <summary>
    /// A spool file from before the hook recorded paths still drains.
    /// </summary>
    /// <remarks>
    /// Not hypothetical: the queue on a real instance held a thousand of these when the second
    /// line was added. An entry with no path is an edit whose target is unknown, which is a
    /// weaker fact than an entry with one but a much stronger one than a parse failure.
    /// </remarks>
    [Fact]
    public void Drain_ReadsATimestampOnlyEntryAsAnEditWithNoPath()
    {
        using var sandbox = new SandboxHome();
        var queueDir = sandbox.Home.QueueDir;
        Directory.CreateDirectory(queueDir);

        WriteSpoolFile(queueDir, DateTime.UtcNow.Ticks, "0000dddd", At(1), path: null);

        var entry = Assert.Single(SpoolReader.Drain(queueDir));

        Assert.Equal(At(1), entry.At);
        Assert.Null(entry.Path);
    }

    [Fact]
    public void Drain_DropsAnUnparseableEntryWithoutStrandingTheOnesBehindIt()
    {
        using var sandbox = new SandboxHome();
        var queueDir = sandbox.Home.QueueDir;
        Directory.CreateDirectory(queueDir);

        var baseTicks = DateTime.UtcNow.Ticks;
        File.WriteAllText(Path.Combine(queueDir, $"{baseTicks}-1-badbad00.spool"), "not a timestamp");
        WriteSpoolFile(queueDir, baseTicks + 1, "9999eeee", At(2), "/repo/survivor.cs");

        var entries = SpoolReader.Drain(queueDir);

        var entry = Assert.Single(entries);
        Assert.Equal("/repo/survivor.cs", entry.Path);

        // Removed as well as skipped: a file that cannot be parsed would otherwise be re-read on
        // every drain forever.
        Assert.Empty(Directory.GetFiles(queueDir));
    }

    [Fact(Timeout = 300_000)]
    public async Task Drain_FileVanishesBetweenEnumerationAndRead_SkipsItWithoutThrowing()
    {
        using var sandbox = new SandboxHome();
        var queueDir = sandbox.Home.QueueDir;
        Directory.CreateDirectory(queueDir);

        var paths = Enumerable.Range(0, 200)
            .Select(i => WriteSpoolFile(
                queueDir, DateTime.UtcNow.Ticks + i, $"{i:x8}", At(i + 1), $"/repo/entry-{i}.cs"))
            .ToList();

        var cancellationToken = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();
        var deleter = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var path in paths)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Windows refuses to delete a file Drain has open — the very race
                        // this test manufactures — and reports the refusal both ways:
                        // sharing violations as IOException, access-denied as
                        // UnauthorizedAccessException (seen on CI). Losing this round is
                        // fine; the loop comes back. Unix unlinks open files, so it never
                        // lands here.
                    }
                }
            }
        }, cancellationToken);

        Exception? exception;
        try
        {
            exception = Record.Exception(() => SpoolReader.Drain(queueDir));
        }
        finally
        {
            cts.Cancel();
            await deleter.WaitAsync(cancellationToken);
        }

        Assert.Null(exception);
    }

    private static DateTimeOffset At(int second) => DateTimeOffset.UnixEpoch.AddSeconds(second);

    private static string WriteSpoolFile(
        string queueDir,
        long ticks,
        string suffix,
        DateTimeOffset at,
        string? path)
    {
        var file = Path.Combine(queueDir, $"{ticks}-1-{suffix}.spool");
        var content = at.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\n";
        File.WriteAllText(file, path is null ? content : content + path + "\n");
        return file;
    }
}
