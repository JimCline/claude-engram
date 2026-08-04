using System.Diagnostics;
using System.Globalization;

namespace Engram.EndToEnd.Tests;

[CollectionDefinition("Hook execution")]
public class HookExecutionCollection
{
}

[Collection("Hook execution")]
public class HookFileTouchedTests
{
    [Fact]
    public void FileTouched_ExitsZero_FiveRunsYieldFiveSpoolFiles()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        for (var i = 0; i < 5; i++)
        {
            var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "hook", "file-touched");
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr);
        }

        var queueDir = Path.Combine(home.Root, "queue");
        var spoolFiles = Directory.GetFiles(queueDir);
        Assert.Equal(5, spoolFiles.Length);
    }

    [Fact(Timeout = 300_000)]
    public async Task FileTouched_FiftyConcurrentProcesses_ProduceFiftyDistinctSpoolFiles_EachWithAParseableTimestamp()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var runs = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => EngramProcess.Run(home.Root, "hook", "file-touched")));
        var results = await Task.WhenAll(runs);

        Assert.All(results, r => Assert.Equal(0, r.ExitCode));

        var queueDir = Path.Combine(home.Root, "queue");
        var spoolFiles = Directory.GetFiles(queueDir);
        Assert.Equal(50, spoolFiles.Length);

        foreach (var file in spoolFiles)
        {
            var content = File.ReadAllText(file).Trim();
            Assert.True(
                DateTime.TryParse(content, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
                $"spool file '{file}' did not contain a parseable ISO-8601 timestamp: '{content}'");
        }
    }

    [Fact]
    public void FileTouched_CompletesInUnder100Milliseconds()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        const int totalRuns = 21;
        var elapsedMs = new List<long>(totalRuns);

        for (var i = 0; i < totalRuns; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var (exitCode, _, _) = EngramProcess.Run(home.Root, "hook", "file-touched");
            stopwatch.Stop();

            Assert.Equal(0, exitCode);
            elapsedMs.Add(stopwatch.ElapsedMilliseconds);
        }

        var queueDir = Path.Combine(home.Root, "queue");
        var spoolFiles = Directory.GetFiles(queueDir);
        Assert.Equal(totalRuns, spoolFiles.Length);

        var samples = elapsedMs.Skip(1).Order().ToList();
        var min = samples[0];
        var median = samples[samples.Count / 2];
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];

        Assert.True(median < 100,
            $"file-touched median took {median}ms across {samples.Count} timed runs (min={min}ms, median={median}ms, p95={p95}ms), expected median < 100ms");
    }
}
