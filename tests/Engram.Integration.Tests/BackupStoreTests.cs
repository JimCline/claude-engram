using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// In the pool collection because restoring must clear the connection pool — a pooled handle on
/// the database being replaced is the one thing that makes a restore silently wrong — and
/// <c>ClearAllPools</c> is process-global, so running these beside the test that asserts pooled
/// reuse makes each flaky in the other's presence.
/// </summary>
[Collection(SqlitePoolCollection.Name)]
public class BackupStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static long Write(SqliteConnection connection, string path, string body, DateTimeOffset at) =>
        FactStore.Remember(connection, new FactWrite(path, "note", "states", body, "project", "stated"), at).FactId;

    private static BackupSettings Settings(
        bool enabled = true,
        int intervalMinutes = 60,
        int hourly = 24,
        int daily = 7,
        int weekly = 4) =>
        new(enabled, intervalMinutes, hourly, daily, weekly, []);

    // --- taking one -------------------------------------------------------------------------

    [Fact]
    public void Take_WritesASnapshotHoldingTheSameFacts()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);

        var snapshot = BackupStore.Take(connection, sandbox.Home, T0);

        using var restored = EngramDatabase.Open(snapshot.Path);
        Assert.Equal(
            "It binds loopback only.",
            Assert.Single(FactStore.SearchRanked(restored, "loopback", 10)) is { } hit
                ? FactStore.ReadById(restored, hit.FactId)!.Body
                : null);
    }

    /// <summary>
    /// The reason this is <c>VACUUM INTO</c> and not <c>File.Copy</c>.
    /// </summary>
    /// <remarks>
    /// <para>In WAL mode a committed fact lives in <c>engram.db-wal</c> until something
    /// checkpoints it, so a copy of <c>engram.db</c> alone is missing recent work — the failure
    /// that looks like a working backup right up until you need it.</para>
    ///
    /// <para>The second assertion is the one that carries the test: it pins the naive version as
    /// actually broken here, rather than asserting the correct version works and calling that
    /// evidence. It turns out to be worse than "stale" — with everything still in the log, the
    /// copied file has no <c>fact</c> table at all, so the loss is total rather than partial.
    /// </para>
    /// </remarks>
    [Fact]
    public void Take_CapturesFactsStillSittingInTheWriteAheadLog()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);

        var snapshot = BackupStore.Take(connection, sandbox.Home, T0);

        var naive = Path.Combine(sandbox.Home.BackupDir, "naive.db");
        File.Copy(sandbox.Home.DatabasePath, naive);

        using (var fromSnapshot = EngramDatabase.Open(snapshot.Path))
        {
            Assert.Single(FactStore.SearchRanked(fromSnapshot, "loopback", 10));
        }

        SqliteConnection.ClearAllPools();
        using var fromNaiveCopy = EngramDatabase.Open(naive);
        Assert.Throws<SqliteException>(() => FactStore.SearchRanked(fromNaiveCopy, "loopback", 10));
    }

    [Fact]
    public void Take_LeavesNoPartialFileBehind()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);

        BackupStore.Take(connection, sandbox.Home, T0);

        Assert.Empty(Directory.EnumerateFiles(sandbox.Home.BackupDir, "*.partial"));
    }

    [Fact]
    public void Take_TwiceInTheSameSecond_KeepsBoth()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);

        BackupStore.Take(connection, sandbox.Home, T0);
        BackupStore.Take(connection, sandbox.Home, T0);

        Assert.Equal(2, BackupStore.List(sandbox.Home).Count);
    }

    [Fact]
    public void Take_NamesTheSchemaVersionItWrote()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var snapshot = BackupStore.Take(connection, sandbox.Home, T0);

        Assert.Equal(EngramDatabase.SchemaVersion, snapshot.SchemaVersion);
        Assert.Contains("-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    // --- deciding whether one is due --------------------------------------------------------

    [Fact]
    public void Due_WithNoSnapshotYet_IsTrue()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);

        Assert.True(BackupStore.Due(sandbox.Home, connection, Settings(), T0).ShouldTake);
    }

    [Fact]
    public void Due_OnAnEmptyStore_IsFalse()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.False(BackupStore.Due(sandbox.Home, connection, Settings(), T0).ShouldTake);
    }

    [Fact]
    public void Due_InsideTheInterval_IsFalseEvenWhenTheStoreChanged()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
        BackupStore.Take(connection, sandbox.Home, T0);

        Write(connection, "/knowledge/testing/merlin", "It stoops at speed.", T0);

        Assert.False(BackupStore.Due(sandbox.Home, connection, Settings(), T0.AddMinutes(59)).ShouldTake);
    }

    /// <summary>
    /// The half that stops an idle machine writing twenty-four identical copies a day.
    /// </summary>
    [Fact]
    public void Due_AfterTheIntervalButWithNothingChanged_IsFalse()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
        BackupStore.Take(connection, sandbox.Home, T0);

        var decision = BackupStore.Due(sandbox.Home, connection, Settings(), T0.AddHours(9));

        Assert.False(decision.ShouldTake);
        Assert.Contains("nothing has changed", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Due_AfterTheIntervalWithANewFact_IsTrue()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
        BackupStore.Take(connection, sandbox.Home, T0);

        Write(connection, "/knowledge/testing/merlin", "It stoops at speed.", T0.AddHours(1));

        Assert.True(BackupStore.Due(sandbox.Home, connection, Settings(), T0.AddHours(2)).ShouldTake);
    }

    /// <summary>
    /// Forgetting changes what the store believes without adding a row anyone counts naively. A
    /// fingerprint that only watched inserts would call this "nothing has changed" and let the
    /// last copy of a retracted fact roll out of retention unnoticed.
    /// </summary>
    [Fact]
    public void Due_AfterAFactIsForgotten_IsTrue()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
        BackupStore.Take(connection, sandbox.Home, T0);

        FactStore.Forget(connection, id, "no longer true", T0.AddHours(1));

        Assert.True(BackupStore.Due(sandbox.Home, connection, Settings(), T0.AddHours(2)).ShouldTake);
    }

    [Fact]
    public void Due_WhenDisabled_IsFalse()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);

        Assert.False(BackupStore.Due(sandbox.Home, connection, Settings(enabled: false), T0).ShouldTake);
    }

    // --- thinning ---------------------------------------------------------------------------

    /// <summary>
    /// Anchored just before the hour so that "an hour ago" lands in the previous hour bucket and
    /// "ten minutes ago" does not. Bucketing is by wall-clock hour, not by elapsed time, and a
    /// fixture anchored on the hour makes those two look the same.
    /// </summary>
    private static readonly DateTimeOffset TFake = new(2026, 8, 6, 12, 59, 0, TimeSpan.Zero);

    private static IReadOnlyList<BackupSnapshot> Fake(params double[] hoursAgo) =>
        FakeAt(hoursAgo.Select(h => TFake.AddHours(-h)));

    private static IReadOnlyList<BackupSnapshot> FakeMinutes(params double[] minutesAgo) =>
        FakeAt(minutesAgo.Select(m => TFake.AddMinutes(-m)));

    private static IReadOnlyList<BackupSnapshot> FakeAt(IEnumerable<DateTimeOffset> times) =>
        times.Select(t => new BackupSnapshot(
            "/tmp/" + BackupStore.NameFor(t, 2),
            t,
            2,
            1024)).ToList();

    /// <summary>
    /// Zero on every limit, which config cannot express — <see cref="BackupSettings.Read"/> floors
    /// them at one — but code can, and does here on purpose. It is the only input that reaches the
    /// unconditional keep, so it is the only input that can prove it is doing anything. With any
    /// limit at one or more the newest snapshot survives via its own hourly bucket regardless, and
    /// a test using those numbers would pass whether or not the line existed.
    /// </summary>
    [Fact]
    public void Plan_KeepsTheNewestSnapshotEvenWhenEveryLimitIsZero()
    {
        var plan = BackupStore.Plan(FakeMinutes(0, 10, 20, 30), Settings(hourly: 0, daily: 0, weekly: 0));

        Assert.Equal(TFake, Assert.Single(plan.Keep).TakenAt);
        Assert.Equal(3, plan.Delete.Count);
    }

    [Fact]
    public void Plan_KeepsTheNewestSnapshotUnderOrdinaryLimits()
    {
        var plan = BackupStore.Plan(FakeMinutes(0, 10, 20, 30), Settings(hourly: 1, daily: 1, weekly: 1));

        Assert.Contains(plan.Keep, s => s.TakenAt == TFake);
        Assert.DoesNotContain(plan.Delete, s => s.TakenAt == TFake);
    }

    /// <summary>Four snapshots inside one hour collapse to the newest of them.</summary>
    [Fact]
    public void Plan_KeepsOnlyTheNewestWithinEachHour()
    {
        var plan = BackupStore.Plan(FakeMinutes(0, 10, 20, 30), Settings(hourly: 24, daily: 7, weekly: 4));

        Assert.Single(plan.Keep);
        Assert.Equal(3, plan.Delete.Count);
    }

    [Fact]
    public void Plan_KeepsOnePerHourUpToTheHourlyLimit()
    {
        var plan = BackupStore.Plan(
            Fake(0, 1, 2, 3, 4, 5),
            Settings(hourly: 3, daily: 1, weekly: 1));

        // Three hourly buckets, plus whatever the daily and weekly buckets pin — all of which
        // resolve to the newest snapshot, which is already kept.
        Assert.Equal(3, plan.Keep.Count);
        Assert.Equal(3, plan.Delete.Count);
    }

    /// <summary>
    /// The generational part: an old snapshot survives because it is the newest of its day, long
    /// after the hourly window has rolled past it.
    /// </summary>
    [Fact]
    public void Plan_KeepsADailySnapshotTheHourlyWindowNoLongerReaches()
    {
        var plan = BackupStore.Plan(
            Fake(0, 1, 2, 30, 54),
            Settings(hourly: 3, daily: 3, weekly: 1));

        Assert.Contains(plan.Keep, s => s.TakenAt == TFake.AddHours(-30));
        Assert.Contains(plan.Keep, s => s.TakenAt == TFake.AddHours(-54));
    }

    [Fact]
    public void Plan_WithNothingToDelete_SaysSo()
    {
        var plan = BackupStore.Plan(Fake(0, 24, 48), Settings(hourly: 24, daily: 7, weekly: 4));

        Assert.Empty(plan.Delete);
        Assert.Equal(3, plan.Keep.Count);
    }

    [Fact]
    public void Prune_DeletesExactlyWhatThePlanNamed()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);

        for (var i = 0; i < 4; i++)
        {
            Write(connection, $"/knowledge/testing/n{i}", $"Fact {i}.", T0);
            BackupStore.Take(connection, sandbox.Home, T0.AddMinutes(i * 20));
        }

        var plan = BackupStore.Plan(BackupStore.List(sandbox.Home), Settings(hourly: 24, daily: 7, weekly: 4));
        BackupStore.Prune(plan);

        Assert.Equal(plan.Keep.Count, BackupStore.List(sandbox.Home).Count);
        Assert.All(plan.Delete, s => Assert.False(File.Exists(s.Path)));
        Assert.All(plan.Keep, s => Assert.True(File.Exists(s.Path)));
    }

    // --- putting one back -------------------------------------------------------------------

    [Fact]
    public void Restore_PutsTheOldFactsBack()
    {
        using var sandbox = new SandboxHome(initialize: false);
        BackupSnapshot snapshot;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
            snapshot = BackupStore.Take(connection, sandbox.Home, T0);

            var doomed = Write(connection, "/knowledge/testing/merlin", "It stoops at speed.", T0);
            FactStore.Forget(connection, doomed, "test", T0);
        }

        SqliteConnection.ClearAllPools();
        BackupStore.Restore(sandbox.Home, snapshot, T0.AddHours(1));

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Single(FactStore.SearchRanked(reopened, "loopback", 10));
        Assert.Empty(FactStore.SearchRanked(reopened, "stoops", 10));
    }

    /// <summary>
    /// Restoring destroys the one copy someone might turn out to have wanted, and they find that
    /// out afterwards. So the current store becomes a snapshot before the incoming one lands.
    /// </summary>
    [Fact]
    public void Restore_KeepsTheStoreItOverwrote()
    {
        using var sandbox = new SandboxHome(initialize: false);
        BackupSnapshot snapshot;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
            snapshot = BackupStore.Take(connection, sandbox.Home, T0);
            Write(connection, "/knowledge/testing/merlin", "It stoops at speed.", T0);
        }

        SqliteConnection.ClearAllPools();
        BackupStore.Restore(sandbox.Home, snapshot, T0.AddHours(1));

        var preserved = BackupStore.List(sandbox.Home)
            .Single(s => s.Name.Contains("pre-restore", StringComparison.Ordinal));

        SqliteConnection.ClearAllPools();
        using var fromPreserved = EngramDatabase.Open(preserved.Path);
        Assert.Single(FactStore.SearchRanked(fromPreserved, "stoops", 10));
    }

    [Fact]
    public void Restore_RefusesASnapshotFromANewerSchemaVersion()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
        }

        SqliteConnection.ClearAllPools();
        var fromTheFuture = new BackupSnapshot("/tmp/nonexistent.db", T0, EngramDatabase.SchemaVersion + 1, 0);

        var exception = Assert.Throws<InvalidOperationException>(
            () => BackupStore.Restore(sandbox.Home, fromTheFuture, T0));

        Assert.Contains("Refusing", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sets up the case a restore is actually reached for: an unopenable store, with a
    /// write-ahead log stranded beside it. Returns a good snapshot taken before the damage.
    /// </summary>
    /// <remarks>
    /// The corruption is what makes this test able to fail. On a healthy store the pre-restore
    /// snapshot opens the database and SQLite checkpoints and unlinks the log on close, so the
    /// stale sidecars are gone before anything explicit runs — an earlier version of this test
    /// passed with the cleanup deleted, proving nothing. Here that open throws, nothing
    /// checkpoints, and the sidecars survive unless the restore removes them itself.
    /// </remarks>
    private static BackupSnapshot BreakTheStore(SandboxHome sandbox)
    {
        BackupSnapshot snapshot;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
            snapshot = BackupStore.Take(connection, sandbox.Home, T0);
        }

        SqliteConnection.ClearAllPools();
        File.WriteAllText(sandbox.Home.DatabasePath, "this is not a database");
        File.WriteAllBytes(sandbox.Home.DatabasePath + "-wal", [0x37, 0x7f, 0x06, 0x82]);
        File.WriteAllBytes(sandbox.Home.DatabasePath + "-shm", [0, 0, 0, 0]);
        return snapshot;
    }

    /// <summary>
    /// A write-ahead log belonging to the database that was just replaced would be replayed into
    /// the one that took its place. That is corruption with a zero exit code.
    /// </summary>
    /// <remarks>
    /// <para>The fixture deletes the database and leaves its log, because that is the only
    /// arrangement in which the explicit cleanup does any work. Anywhere else, something opens
    /// SQLite on the way past — the pre-restore snapshot does — and SQLite unlinks the sidecars
    /// itself on close. Two earlier versions of this test passed with the cleanup deleted for
    /// exactly that reason.</para>
    ///
    /// <para>It is not a contrived arrangement. A database file disappearing while its log and
    /// the rest of the home stay put is a state this project has actually been found in.</para>
    /// </remarks>
    [Fact]
    public void Restore_WhenOnlyTheLogSurvived_RemovesItRatherThanReplayingIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        BackupSnapshot snapshot;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
            snapshot = BackupStore.Take(connection, sandbox.Home, T0);
        }

        SqliteConnection.ClearAllPools();
        File.Delete(sandbox.Home.DatabasePath);
        File.WriteAllBytes(sandbox.Home.DatabasePath + "-wal", [0x37, 0x7f, 0x06, 0x82]);
        File.WriteAllBytes(sandbox.Home.DatabasePath + "-shm", [0, 0, 0, 0]);

        BackupStore.Restore(sandbox.Home, snapshot, T0.AddHours(1));

        Assert.False(File.Exists(sandbox.Home.DatabasePath + "-wal"));
        Assert.False(File.Exists(sandbox.Home.DatabasePath + "-shm"));
    }

    [Fact]
    public void Restore_WhenTheStoreIsGoneEntirely_PutsItBack()
    {
        using var sandbox = new SandboxHome(initialize: false);
        BackupSnapshot snapshot;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/knowledge/testing/kestrel", "It binds loopback only.", T0);
            snapshot = BackupStore.Take(connection, sandbox.Home, T0);
        }

        SqliteConnection.ClearAllPools();
        File.Delete(sandbox.Home.DatabasePath);

        BackupStore.Restore(sandbox.Home, snapshot, T0.AddHours(1));

        SqliteConnection.ClearAllPools();
        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Single(FactStore.SearchRanked(reopened, "loopback", 10));
    }

    [Fact]
    public void Restore_OverACorruptStore_StillYieldsAWorkingOne()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var snapshot = BreakTheStore(sandbox);

        BackupStore.Restore(sandbox.Home, snapshot, T0.AddHours(1));

        SqliteConnection.ClearAllPools();
        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Single(FactStore.SearchRanked(reopened, "loopback", 10));
    }

    /// <summary>
    /// A store worth restoring over is disproportionately likely to be one that will not open —
    /// that is often why someone is restoring. Preserving it must not depend on it being healthy.
    /// </summary>
    [Fact]
    public void Restore_OverACorruptStore_KeepsTheBytesItCouldNotRead()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var snapshot = BreakTheStore(sandbox);

        BackupStore.Restore(sandbox.Home, snapshot, T0.AddHours(1));

        var kept = Assert.Single(Directory.EnumerateFiles(sandbox.Home.BackupDir, "unreadable-*.db"));
        Assert.Equal("this is not a database", File.ReadAllText(kept));
    }

    // --- one at a time ----------------------------------------------------------------------

    /// <summary>
    /// Sessions start in bursts — a fleet of subagents, a script opening several at once — and
    /// each spawns a backup. Without this, thirty arrive together and run thirty VACUUMs over one
    /// database to produce thirty files the next prune all but deletes.
    /// </summary>
    [Fact]
    public void TryLock_WhileAnotherHolderExists_ReturnsNull()
    {
        using var sandbox = new SandboxHome(initialize: false);

        using var first = BackupStore.TryLock(sandbox.Home);

        Assert.NotNull(first);
        Assert.Null(BackupStore.TryLock(sandbox.Home));
    }

    [Fact]
    public void TryLock_AfterTheHolderReleases_SucceedsAgain()
    {
        using var sandbox = new SandboxHome(initialize: false);

        BackupStore.TryLock(sandbox.Home)!.Dispose();

        using var second = BackupStore.TryLock(sandbox.Home);
        Assert.NotNull(second);
    }

    [Fact]
    public void TheLock_IsNotMistakenForASnapshot()
    {
        using var sandbox = new SandboxHome(initialize: false);

        using var held = BackupStore.TryLock(sandbox.Home);

        Assert.Empty(BackupStore.List(sandbox.Home));
    }

    // --- naming -----------------------------------------------------------------------------

    [Fact]
    public void List_IgnoresFilesThatAreNotSnapshots()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Directory.CreateDirectory(sandbox.Home.BackupDir);
        File.WriteAllText(Path.Combine(sandbox.Home.BackupDir, "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(sandbox.Home.BackupDir, "unreadable-20260806T120000Z.db"), "bytes");
        File.WriteAllText(Path.Combine(sandbox.Home.BackupDir, BackupStore.NameFor(T0, 2)), "bytes");

        Assert.Single(BackupStore.List(sandbox.Home));
    }

    [Fact]
    public void List_OrdersNewestFirst()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Directory.CreateDirectory(sandbox.Home.BackupDir);
        foreach (var hours in new[] { 3, 1, 2 })
        {
            File.WriteAllText(Path.Combine(sandbox.Home.BackupDir, BackupStore.NameFor(T0.AddHours(-hours), 2)), "b");
        }

        var listed = BackupStore.List(sandbox.Home);

        Assert.Equal(T0.AddHours(-1), listed[0].TakenAt);
        Assert.Equal(T0.AddHours(-3), listed[2].TakenAt);
    }
}
