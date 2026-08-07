using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Folding the <c>file-touched</c> queue down to one entry per file.
/// </summary>
/// <remarks>
/// Tier 2 because every claim here is about a directory: which files survive, which are deleted,
/// and what happens when one of them cannot be read. None of that is reachable from a unit test
/// with the filesystem mocked out, and the filesystem is the entire subject.
/// </remarks>
public class SpoolCompactorTests
{
    private static string Spool(string queueDir, DateTimeOffset at, string? path)
    {
        Directory.CreateDirectory(queueDir);

        // Named the way the hook names them, because the compactor's ordering and Drain's both
        // depend on the leading ticks sorting lexicographically.
        var file = Path.Combine(queueDir, $"{at.UtcDateTime.Ticks}-{Environment.ProcessId}-{Guid.NewGuid():N}.spool");
        var body = at.ToString("o") + "\n" + (path is null ? string.Empty : path + "\n");
        File.WriteAllText(file, body);

        return file;
    }

    private static DateTimeOffset At(int minute) => new(2026, 8, 6, 12, minute, 0, TimeSpan.Zero);

    private static int Count(string queueDir) => Directory.GetFiles(queueDir, "*.spool").Length;

    /// <summary>What any consumer sees: files in name order, which is write order.</summary>
    private static List<SpooledEdit> ReadInNameOrder(string queueDir)
    {
        var files = Directory.GetFiles(queueDir, "*.spool");
        Array.Sort(files, StringComparer.Ordinal);

        return files
            .Select(file => SpoolReader.Parse(File.ReadAllText(file)))
            .OfType<SpooledEdit>()
            .ToList();
    }

    [Fact]
    public void BelowTheThreshold_ItReadsNothingAndDeletesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        for (var i = 0; i < 10; i++)
        {
            Spool(queue, At(i), "/repo/same.cs");
        }

        var result = SpoolCompactor.Compact(queue, apply: true);

        // Ten entries for one file are nine redundant ones, and it still leaves them: the
        // threshold is what keeps the automatic pass free in the steady state.
        Assert.Equal(10, Count(queue));
        Assert.Equal(10, result.Before);
        Assert.Equal(10, result.Kept);
        Assert.False(result.Changes);
    }

    [Fact]
    public void Forced_ItKeepsTheNewestEntryForEachFile()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        Spool(queue, At(1), "/repo/a.cs");
        Spool(queue, At(2), "/repo/b.cs");
        var newestA = Spool(queue, At(3), "/repo/a.cs");
        var newestB = Spool(queue, At(4), "/repo/b.cs");

        var result = SpoolCompactor.Compact(queue, apply: true, force: true);

        Assert.Equal(2, result.Paths);
        Assert.Equal(2, result.Superseded);
        Assert.Equal(new[] { newestA, newestB }.Order(StringComparer.Ordinal), Directory.GetFiles(queue).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The asymmetry, and why it is not an inconsistency.
    /// </summary>
    /// <remarks>
    /// A path entry keeps its newest because the timestamp there means "last touched" and the
    /// content is read fresh regardless. A pathless entry keeps its oldest because a bare timestamp
    /// is only ever a watermark — there are unindexed changes at least this old — and the earlier
    /// one is the safe reading of a set of them.
    /// </remarks>
    [Fact]
    public void PathlessEntries_CollapseToTheOldestOfThem()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        var oldest = Spool(queue, At(1), path: null);
        Spool(queue, At(5), path: null);
        Spool(queue, At(9), path: null);

        var result = SpoolCompactor.Compact(queue, apply: true, force: true);

        Assert.True(result.Pathless);
        Assert.Equal(0, result.Paths);
        Assert.Equal(oldest, Assert.Single(Directory.GetFiles(queue)));
    }

    [Fact]
    public void ADryRun_PredictsTheApplyExactlyAndDeletesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        for (var i = 0; i < 12; i++)
        {
            Spool(queue, At(i), $"/repo/{i % 4}.cs");
        }

        var planned = SpoolCompactor.Compact(queue, apply: false, force: true);
        Assert.Equal(12, Count(queue));

        var applied = SpoolCompactor.Compact(queue, apply: true, force: true);

        Assert.Equal(planned, applied);
        Assert.Equal(4, Count(queue));
    }

    [Fact]
    public void AnEntryThatParsesToNothing_IsDeletedBecauseDrainWouldDropItAnyway()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        Directory.CreateDirectory(queue);
        File.WriteAllText(Path.Combine(queue, "0000000000-garbage.spool"), "not a timestamp");
        var real = Spool(queue, At(1), "/repo/a.cs");

        var result = SpoolCompactor.Compact(queue, apply: true, force: true);

        Assert.Equal(1, result.Unparseable);
        Assert.Equal(real, Assert.Single(Directory.GetFiles(queue)));
    }

    /// <summary>
    /// Unreadable is not unparseable, and the difference decides whether an edit is destroyed.
    /// </summary>
    /// <remarks>
    /// The writer holds <c>FileShare.None</c>, so a compaction racing an edit can be refused the
    /// read. Deleting on a transient error would discard an entry that was perfectly good.
    /// </remarks>
    [Fact]
    public void AnEntryItCannotOpen_IsLeftAloneRatherThanDeleted()
    {
        // The if/else rather than Assert.SkipWhen is for CA1416: the analyzer recognises an
        // OperatingSystem check as a platform guard and a skip helper as an ordinary call.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("chmod is how this test makes a file unreadable");
        }
        else
        {
            using var sandbox = new SandboxHome(initialize: false);
            var queue = sandbox.Home.QueueDir;

            var unreadable = Spool(queue, At(1), "/repo/a.cs");
            Spool(queue, At(2), "/repo/a.cs");

            File.SetUnixFileMode(unreadable, UnixFileMode.None);
            try
            {
                var result = SpoolCompactor.Compact(queue, apply: true, force: true);

                Assert.Equal(1, result.Unreadable);
                Assert.Equal(0, result.Unparseable);
                Assert.True(File.Exists(unreadable));
            }
            finally
            {
                File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public void TheDistinctPathCeiling_KeepsTheNewestAndReportsWhatItDropped()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        for (var i = 0; i < 5; i++)
        {
            Spool(queue, At(i), $"/repo/{i}.cs");
        }

        var result = SpoolCompactor.Compact(queue, apply: true, force: true, maxPaths: 2);

        Assert.Equal(2, result.Paths);
        Assert.Equal(3, result.Dropped);
        Assert.Equal(2, Count(queue));

        // The newest two, not an arbitrary two.
        var survivors = ReadInNameOrder(queue).Select(edit => edit.Path).ToList();
        Assert.Equal(new[] { "/repo/3.cs", "/repo/4.cs" }, survivors);
    }

    [Fact]
    public void EveryFileItSawIsAccountedForInTheCounts()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        for (var i = 0; i < 9; i++)
        {
            Spool(queue, At(i), $"/repo/{i % 3}.cs");
        }

        Spool(queue, At(20), path: null);
        Spool(queue, At(21), path: null);
        File.WriteAllText(Path.Combine(queue, "0000000000-garbage.spool"), "junk");

        var result = SpoolCompactor.Compact(queue, apply: true, force: true, maxPaths: 2);

        Assert.Equal(12, result.Before);
        Assert.Equal(result.Before, result.Kept + result.Superseded + result.Unparseable + result.Dropped);
        Assert.Equal(result.Kept, Count(queue));
    }

    [Fact]
    public void Compacting_Twice_FindsNothingLeftToDo()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        for (var i = 0; i < 20; i++)
        {
            Spool(queue, At(i), $"/repo/{i % 3}.cs");
        }

        SpoolCompactor.Compact(queue, apply: true, force: true);
        var second = SpoolCompactor.Compact(queue, apply: true, force: true);

        Assert.False(second.Changes);
        Assert.Equal(3, second.Kept);
    }

    /// <summary>
    /// Compaction must not break the reader that comes after it.
    /// </summary>
    /// <remarks>
    /// It only deletes, never renames, so surviving names still lead with ticks and a
    /// name-ordered read is still chronological. That is an invariant worth asserting rather
    /// than trusting, because a future compactor tempted to rewrite entries into one file
    /// would pass every other test here.
    /// </remarks>
    [Fact]
    public void WhatSurvives_StillDrainsInChronologicalOrder()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = sandbox.Home.QueueDir;

        Spool(queue, At(9), "/repo/c.cs");
        Spool(queue, At(1), "/repo/a.cs");
        Spool(queue, At(5), "/repo/b.cs");
        Spool(queue, At(2), "/repo/a.cs");

        SpoolCompactor.Compact(queue, apply: true, force: true);

        var drained = ReadInNameOrder(queue);

        Assert.Equal(new[] { At(2), At(5), At(9) }, drained.Select(edit => edit.At));
        Assert.Equal(new[] { "/repo/a.cs", "/repo/b.cs", "/repo/c.cs" }, drained.Select(edit => edit.Path));
    }

    [Fact]
    public void AMissingQueueDirectory_IsAnEmptyCompactionRatherThanAThrow()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var queue = Path.Combine(sandbox.Home.Root, "no-such-queue");

        var result = SpoolCompactor.Compact(queue, apply: true, force: true);

        Assert.Equal(0, result.Before);
        Assert.False(result.Changes);
    }
}
