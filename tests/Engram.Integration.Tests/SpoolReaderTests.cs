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
        WriteSpoolFile(queueDir, baseTicks, "1111aaaa", "first");
        WriteSpoolFile(queueDir, baseTicks + 1, "2222bbbb", "second");
        WriteSpoolFile(queueDir, baseTicks + 2, "3333cccc", "third");

        var entries = SpoolReader.Drain(queueDir);

        Assert.Equal(["first", "second", "third"], entries);
        Assert.Empty(Directory.GetFiles(queueDir));
    }

    [Fact(Timeout = 300_000)]
    public async Task Drain_FileVanishesBetweenEnumerationAndRead_SkipsItWithoutThrowing()
    {
        using var sandbox = new SandboxHome();
        var queueDir = sandbox.Home.QueueDir;
        Directory.CreateDirectory(queueDir);

        var paths = Enumerable.Range(0, 200)
            .Select(i => WriteSpoolFile(queueDir, DateTime.UtcNow.Ticks + i, $"{i:x8}", $"entry-{i}"))
            .ToList();

        var cancellationToken = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();
        var deleter = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var path in paths)
                {
                    File.Delete(path);
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

    private static string WriteSpoolFile(string queueDir, long ticks, string suffix, string content)
    {
        var path = Path.Combine(queueDir, $"{ticks}-1-{suffix}.spool");
        File.WriteAllText(path, content);
        return path;
    }
}
