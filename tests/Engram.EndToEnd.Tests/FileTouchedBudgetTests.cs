using System.Diagnostics;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// D4 rule 4 against the published binary: <c>file-touched</c> does not open the database,
/// and one spool file per invocation is what makes that safe under concurrency.
/// </summary>
public class FileTouchedBudgetTests
{
    private const int Warmup = 20;
    private const int Samples = 100;

    /// <summary>
    /// Measured on this machine: a database open costs 2.1–2.4 ms (session-start +2.38,
    /// user-prompt +2.12 against the same floor) and <c>file-touched</c> costs +0.02. One
    /// millisecond sits an order of magnitude above the real cost and comfortably below the
    /// cheapest violation, so this fails when the rule is broken and not when the machine is
    /// merely busy.
    /// </summary>
    private const double MaxMarginalCostMs = 1.0;

    /// <summary>
    /// The hook's 10 ms budget is almost entirely process start, so asserting the absolute
    /// number would measure the machine. What the code controls is the difference between
    /// this hook and starting the binary to do nothing, and that difference is the rule.
    /// </summary>
    [Fact]
    public void FileTouched_CostsNothingMeasurableOverStartingTheBinary()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        for (var i = 0; i < Warmup; i++)
        {
            TimeNoOp(home.Root);
            TimeFileTouched(home.Root);
        }

        var floor = new List<double>(Samples);
        var hook = new List<double>(Samples);

        // Interleaved rather than one batch after the other: a machine that gets busier
        // partway through then slows both arms equally instead of moving the difference this
        // asserts on.
        for (var i = 0; i < Samples; i++)
        {
            floor.Add(TimeNoOp(home.Root));
            hook.Add(TimeFileTouched(home.Root));
        }

        var floorMedian = Median(floor);
        var hookMedian = Median(hook);
        var marginal = hookMedian - floorMedian;

        Assert.True(
            marginal < MaxMarginalCostMs,
            $"file-touched cost {marginal:0.00} ms more than starting the binary to do nothing "
                + $"(p50 {hookMedian:0.00} ms against a floor of {floorMedian:0.00} ms). D4 rule 4 gives it a "
                + "10 ms budget that is almost all process start, so anything measurable here — an "
                + "opened database above all — is most of the headroom.");
    }

    /// <summary>
    /// One spool file per invocation, so concurrent hooks cannot lose each other's records.
    /// </summary>
    /// <remarks>
    /// The alternative D4 rejected is a single shared spool: <c>FileMode.Append</c> is
    /// seek-then-write rather than POSIX <c>O_APPEND</c>, so two hooks can resolve the same
    /// end-of-file offset and one record vanishes. A multi-file edit fires this hook once per
    /// file at once, which is exactly the case that would lose one.
    /// </remarks>
    [Fact]
    public void EightConcurrentTouches_EachLeaveTheirOwnRecord()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var queue = Path.Combine(home.Root, "queue");
        var before = Directory.Exists(queue) ? Directory.GetFiles(queue).Length : 0;

        const int Width = 8;
        const int Each = 4;

        Parallel.For(0, Width, _ =>
        {
            for (var i = 0; i < Each; i++)
            {
                EngramProcess.Run(home.Root, "hook", "file-touched");
            }
        });

        var written = Directory.GetFiles(queue).Length - before;

        Assert.Equal(Width * Each, written);
    }

    private static double TimeNoOp(string home) => Time(home, "home");

    private static double TimeFileTouched(string home) => Time(home, "hook", "file-touched");

    private static double Time(string home, params string[] args)
    {
        var stopwatch = Stopwatch.StartNew();
        EngramProcess.Run(home, args);
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;

        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}
