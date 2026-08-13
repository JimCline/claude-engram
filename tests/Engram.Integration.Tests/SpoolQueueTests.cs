using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// SpoolQueue's contract, pinned directly for the first time — until --drain-all every claim
/// about it rode through SpoolCompactor, SpoolReader, or CodeIndexer, none of which exercises
/// Consume's non-mutation guarantee or WithoutPathless's exact filter (spec §8.2/§8.3 guard 15).
/// </summary>
public class SpoolQueueTests
{
    private static void Spool(string queueDir, DateTimeOffset at, string? path)
    {
        Directory.CreateDirectory(queueDir);
        var file = Path.Combine(queueDir, $"{at.UtcDateTime.Ticks}-{Environment.ProcessId}-{Guid.NewGuid():N}.spool");
        File.WriteAllText(file, at.ToString("o") + "\n" + (path is null ? string.Empty : path + "\n"));
    }

    private static DateTimeOffset At(int minute) => new(2026, 8, 6, 12, minute, 0, TimeSpan.Zero);

    /// <summary>
    /// Consume deletes files but must never remove from the in-memory snapshot — that is what
    /// makes two views derived from one Peek() safe to drain independently against (§8.1). A
    /// --drain-all pass depends on exactly this: WithoutPathless() is taken from the same
    /// captured list the invoked root already consumed against, so a Consume that mutated
    /// entries would make the next caller's Pathless/Under/LeftBehind silently disagree with
    /// what it just reported. Do not "fix" the snapshot behavior below into a stateful one —
    /// that would reintroduce the very bug this test exists to catch.
    /// </summary>
    [Fact]
    public void Consume_DeletesFilesButLeavesTheSnapshotUnchanged()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var dir = sandbox.Home.QueueDir;

        Spool(dir, At(1), "/repo/a.cs");
        Spool(dir, At(2), "/repo/b.cs");
        Spool(dir, At(3), path: null);
        Spool(dir, At(4), "/other/c.cs");

        var queue = SpoolQueue.Peek(dir);

        var pathlessBefore = queue.Pathless;
        var underBefore = queue.Under("/repo");
        var leftBehindBefore = queue.LeftBehind("/repo");

        var consumed = queue.Consume("/repo", consumePathless: true);

        Assert.Equal(3, consumed);
        Assert.Single(Directory.GetFiles(dir, "*.spool"));

        Assert.Equal(pathlessBefore, queue.Pathless);
        Assert.Equal(underBefore, queue.Under("/repo"));
        Assert.Equal(leftBehindBefore, queue.LeftBehind("/repo"));
    }

    /// <summary>
    /// WithoutPathless() must drop pathless entries and nothing else — a --drain-all secondary
    /// root reads this view precisely so it can still see everything else a normal drain would
    /// (D41), and a filter that over-removed would silently starve a secondary root's drain.
    /// </summary>
    [Fact]
    public void WithoutPathless_DropsOnlyThePathlessEntries()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var dir = sandbox.Home.QueueDir;

        Spool(dir, At(1), "/repo/a.cs");
        Spool(dir, At(2), "/other/b.cs");
        Spool(dir, At(3), path: null);
        Spool(dir, At(4), path: null);

        var queue = SpoolQueue.Peek(dir);
        var underBefore = queue.Under("/repo");
        var leftBehindBefore = queue.LeftBehind("/repo");

        var view = queue.WithoutPathless();

        Assert.Equal(0, view.Pathless);
        Assert.Equal(underBefore, view.Under("/repo"));
        Assert.Equal(leftBehindBefore, view.LeftBehind("/repo"));
    }

    /// <summary>
    /// A garbage entry carries no path to match against any root, so it can never be "left
    /// behind for the right repo" the way an unreadable or misrooted entry is — the only sound
    /// reading is that any root passing through may take it out of the queue.
    /// </summary>
    [Fact]
    public void AGarbageEntry_IsConsumableFromAnyRoot()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var dir = sandbox.Home.QueueDir;

        Directory.CreateDirectory(dir);
        var garbage = Path.Combine(dir, "0000000000-garbage.spool");
        File.WriteAllText(garbage, "not a timestamp");

        var queue = SpoolQueue.Peek(dir);

        var consumed = queue.Consume("/completely/unrelated/root", consumePathless: false);

        Assert.Equal(1, consumed);
        Assert.False(File.Exists(garbage));
    }

    /// <summary>
    /// The complement of a --drain-all pass's serviced roots is exactly what step 3 discards
    /// (§6.3e). A path under a serviced root must survive; one under none of them must not.
    /// </summary>
    [Fact]
    public void DiscardExcept_DeletesAPathedEntryUnderNoServicedRoot_AndKeepsOneThatIs()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var dir = sandbox.Home.QueueDir;

        Spool(dir, At(1), "/repo/a.cs");
        Spool(dir, At(2), "/other/b.cs");

        var queue = SpoolQueue.Peek(dir);
        var serviced = Directory.GetFiles(dir, "*.spool").Single(f => File.ReadAllText(f).Contains("/repo/a.cs"));

        var discarded = queue.DiscardExcept(["/repo"]);

        Assert.Equal(1, discarded);
        Assert.True(File.Exists(serviced));
        Assert.Single(Directory.GetFiles(dir, "*.spool"));
    }

    /// <summary>
    /// The unconditional pathless skip is what makes step 3 a one-liner callable on the full
    /// snapshot rather than a WithoutPathless() view (§6.3e) — asserted on the full snapshot for
    /// exactly that reason.
    /// </summary>
    [Fact]
    public void DiscardExcept_DoesNotDeleteAPathlessEntry()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var dir = sandbox.Home.QueueDir;

        Spool(dir, At(1), path: null);

        var queue = SpoolQueue.Peek(dir);

        var discarded = queue.DiscardExcept(["/repo"]);

        Assert.Equal(0, discarded);
        Assert.Single(Directory.GetFiles(dir, "*.spool"));
    }

    /// <summary>
    /// The arm most likely to be got wrong and the only one that destroys data: an unreadable
    /// entry carries no trustworthy path, so deleting it on a transient collision would destroy
    /// a possibly-good edit record rather than a stale one (D41).
    /// </summary>
    [Fact]
    public void DiscardExcept_DoesNotDeleteAnUnreadableEntry()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var dir = sandbox.Home.QueueDir;

        Directory.CreateDirectory(dir);
        var locked = Path.Combine(dir, "0000000000-locked.spool");
        File.WriteAllText(locked, "irrelevant");

        SpoolQueue queue;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            queue = SpoolQueue.Peek(dir);
        }

        // Pins the fixture itself: without this, a FileShare collision that fails to make
        // ReadAllText throw would silently reclassify the entry as Garbage instead, and the
        // assertions below would pass without ever exercising the Unreadable path.
        Assert.Equal(1, queue.LeftBehind("/repo"));

        var discarded = queue.DiscardExcept(["/repo"]);

        Assert.Equal(0, discarded);
        Assert.True(File.Exists(locked));
    }

    /// <summary>
    /// Garbage is Consume's job, not DiscardExcept's — leaving it alone here changes no count
    /// that anything already reports (§6.3e).
    /// </summary>
    [Fact]
    public void DiscardExcept_LeavesAGarbageEntryAlone()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var dir = sandbox.Home.QueueDir;

        Directory.CreateDirectory(dir);
        var garbage = Path.Combine(dir, "0000000000-garbage.spool");
        File.WriteAllText(garbage, "not a timestamp");

        var queue = SpoolQueue.Peek(dir);

        var discarded = queue.DiscardExcept(["/repo"]);

        Assert.Equal(0, discarded);
        Assert.True(File.Exists(garbage));
    }
}
