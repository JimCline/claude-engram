namespace Engram.EndToEnd.Tests;

/// <summary>
/// <c>engram queue</c>, and the session-start housekeeping that runs it.
/// </summary>
public class QueueCommandTests
{
    private const int AboveThreshold = 300;

    private static void Spool(string root, int minute, string path)
    {
        var queue = Path.Combine(root, "queue");
        Directory.CreateDirectory(queue);

        var at = new DateTimeOffset(2026, 8, 6, 12, minute % 60, minute / 60 % 60, TimeSpan.Zero);
        var file = Path.Combine(queue, $"{at.UtcDateTime.Ticks}-{minute:D6}.spool");
        File.WriteAllText(file, at.ToString("o") + "\n" + path + "\n");
    }

    private static int Count(string root) =>
        Directory.Exists(Path.Combine(root, "queue"))
            ? Directory.GetFiles(Path.Combine(root, "queue"), "*.spool").Length
            : 0;

    [Fact]
    public void Queue_OnAFreshHome_SaysTheQueueIsEmpty()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "queue");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("empty", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueCompact_WithoutApply_ReportsWhatItWouldDoAndDeletesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        for (var i = 0; i < 30; i++)
        {
            Spool(home.Root, i, $"/repo/{i % 3}.cs");
        }

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "queue", "compact");

        Assert.Equal(0, exitCode);
        Assert.Contains("Would remove", stdout, StringComparison.Ordinal);
        Assert.Contains("Dry run only", stdout, StringComparison.Ordinal);
        Assert.Equal(30, Count(home.Root));
    }

    /// <summary>
    /// Typing the command means the threshold does not apply.
    /// </summary>
    /// <remarks>
    /// Thirty entries are well under <c>SpoolCompactor.Threshold</c>, which exists to keep the
    /// automatic pass free — not to refuse a person who asked for compaction outright.
    /// </remarks>
    [Fact]
    public void QueueCompact_WithApply_FoldsTheQueueToOneEntryPerFile()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        for (var i = 0; i < 30; i++)
        {
            Spool(home.Root, i, $"/repo/{i % 3}.cs");
        }

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "queue", "compact", "--apply");

        Assert.Equal(0, exitCode);
        Assert.Contains("Removed 27 superseded", stdout, StringComparison.Ordinal);
        Assert.Equal(3, Count(home.Root));
    }

    [Fact]
    public void QueueCompact_WithIfLarge_LeavesASmallQueueAlone()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        for (var i = 0; i < 30; i++)
        {
            Spool(home.Root, i, "/repo/same.cs");
        }

        var (exitCode, _, _) = EngramProcess.Run(home.Root, "queue", "compact", "--apply", "--if-large");

        Assert.Equal(0, exitCode);
        Assert.Equal(30, Count(home.Root));
    }

    [Fact]
    public void QueueWithAnUnknownSubcommand_ExitsTwo()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "queue", "nonsense");

        Assert.Equal(2, exitCode);
        Assert.Contains("unknown queue subcommand", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wiring, which is the only part that makes the bound automatic.
    /// </summary>
    /// <remarks>
    /// <para>Everything else here proves the compactor works when someone types the command. This
    /// proves nobody has to. Without it the queue is bounded only by a person noticing, which is
    /// the state this change exists to leave behind.</para>
    ///
    /// <para>It polls rather than sleeping a fixed interval because the child is detached: the
    /// hook returns before any of the work happens, by design, and the machine decides when the
    /// fork gets scheduled. A fixed sleep would be either flaky or slow, and picking which is not a
    /// choice worth making when polling is neither.</para>
    /// </remarks>
    [Fact]
    public void SessionStart_SpawnsHousekeepingThatCompactsAnOversizedQueue()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        for (var i = 0; i < AboveThreshold; i++)
        {
            Spool(home.Root, i, $"/repo/{i % 5}.cs");
        }

        var (exitCode, _, _) = EngramProcess.Run(home.Root, "hook", "session-start");
        Assert.Equal(0, exitCode);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Count(home.Root) > 5 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        Assert.Equal(5, Count(home.Root));
    }
}
