using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

[CollectionDefinition("Hook execution")]
public class HookExecutionCollection
{
}

[Collection("Hook execution")]
public class HookFileTouchedTests
{
    [Fact]
    public void FileTouched_ExitsZero_FiveRunsYieldFiveSpoolLines()
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
        var spoolFile = Directory.GetFiles(queueDir).Single();
        var lines = File.ReadAllLines(spoolFile);
        Assert.Equal(5, lines.Length);
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
        var spoolFile = Directory.GetFiles(queueDir).Single();
        var lines = File.ReadAllLines(spoolFile);
        Assert.Equal(totalRuns, lines.Length);

        var samples = elapsedMs.Skip(1).Order().ToList();
        var min = samples[0];
        var median = samples[samples.Count / 2];
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];

        Assert.True(median < 100,
            $"file-touched median took {median}ms across {samples.Count} timed runs (min={min}ms, median={median}ms, p95={p95}ms), expected median < 100ms");
    }
}
