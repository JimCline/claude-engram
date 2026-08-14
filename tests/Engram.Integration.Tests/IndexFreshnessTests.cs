using System.Collections.Concurrent;
using Engram.Cli;
using Engram.Core;
using Microsoft.Extensions.Logging;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. The background freshness loop's selection, its one-repo-per-tick bound, and the
/// off-by-default config split — the Core loop (<see cref="IndexFreshness"/>) tested directly, the
/// hosted wrapper (<see cref="IndexFreshnessService"/>) tested the way <c>WebhookServiceTests</c>
/// tests <c>WebhookService</c>: instantiated directly, started, and waited on through its own
/// startup line rather than through <c>StartAsync</c>'s weaker promise (spec §7, Commit F).
/// </summary>
public class IndexFreshnessTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    // RepoEnrollment.Enroll canonicalizes the root it stores (PathCanonicalizer.Canonical strips
    // macOS's /private symlink prefix), and CodeIndexer.Index later recomputes identity from that
    // stored (canonical) root. Computing identity from the pre-canonical root here would enroll
    // under a string CodeIndexer never recomputes, so the tick would stamp a row that does not
    // exist. Canonicalize first so the enrolled identity is the one a real tick actually reaches.
    private static string MakeRepoDir(SandboxHome sandbox, out string identity)
    {
        var root = Path.Combine(sandbox.Home.Root, "repos", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.cs"), "class C {}\n");
        var canonical = PathCanonicalizer.Canonical(root);
        identity = CodeIndexer.ResolveIdentity(canonical);
        return canonical;
    }

    private static async Task<bool> Settles(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return condition();
    }

    // ---- IndexFreshness (Core loop): selection and the one-repo-per-tick bound ----

    [Fact]
    public void TickOnce_WithTwoDueRepos_ServicesOnlyTheMoreNeglectedOne()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        var rootA = MakeRepoDir(sandbox, out var identityA);
        var rootB = MakeRepoDir(sandbox, out var identityB);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, identityA, rootA, now.AddDays(-1));
            RepoEnrollment.Enroll(connection, identityB, rootB, now.AddDays(-2));
        }

        new IndexFreshness(sandbox.Home).TickOnce();

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);

        // repo B was decided earlier, so RepoFreshness's most-neglected-first order picks it —
        // and only it: a tick that serviced both would be an unbounded pass wearing this bound's
        // costume.
        Assert.NotNull(RepoEnrollment.Get(check, identityB)?.LastFullScanAt);
        Assert.Null(RepoEnrollment.Get(check, identityA)?.LastFullScanAt);
    }

    [Fact]
    public void TickOnce_CalledTwice_ConvergesOnBothRepos()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        var rootA = MakeRepoDir(sandbox, out var identityA);
        var rootB = MakeRepoDir(sandbox, out var identityB);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, identityA, rootA, now.AddDays(-1));
            RepoEnrollment.Enroll(connection, identityB, rootB, now.AddDays(-2));
        }

        var freshness = new IndexFreshness(sandbox.Home);
        freshness.TickOnce();
        freshness.TickOnce();

        using var check = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.NotNull(RepoEnrollment.Get(check, identityA)?.LastFullScanAt);
        Assert.NotNull(RepoEnrollment.Get(check, identityB)?.LastFullScanAt);
    }

    [Fact]
    public void TickOnce_WithNothingDue_PublishesIdleAndWritesNothing()
    {
        using var sandbox = new SandboxHome();

        new IndexFreshness(sandbox.Home).TickOnce();

        var progress = IndexProgress.Read(sandbox.Home);
        Assert.NotNull(progress);
        Assert.Equal("idle", progress.Outcome);
        Assert.Null(progress.Repo);
    }

    // ---- IndexFreshnessService (hosted wrapper): the config split ----

    private sealed class Captured : ILogger<IndexFreshnessService>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Lines.Enqueue(formatter(state, exception));
        }
    }

    private static void Configure(SandboxHome sandbox, bool autoIndexInBackground) =>
        File.WriteAllText(
            sandbox.Home.ConfigPath,
            $"[indexing]\nauto_index_in_background = {(autoIndexInBackground ? "true" : "false")}\n");

    [Fact]
    public async Task ExecuteAsync_WithTheSettingOff_WritesAnUnavailableNoteNamingItAndDoesNoWork()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        var root = MakeRepoDir(sandbox, out var identity);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, identity, root, now.AddDays(-1));
        }

        Configure(sandbox, autoIndexInBackground: false);

        var log = new Captured();
        var service = new IndexFreshnessService(sandbox.Home, log);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // A decline returns before ticking anything, but StartAsync only promises ExecuteAsync
            // was handed to the scheduler (D55) — so even the quick path is awaited for, not
            // assumed to have completed inline.
            Assert.True(
                await Settles(() => IndexProgress.Read(sandbox.Home) is not null),
                "the freshness service never wrote its decline note");

            var progress = IndexProgress.Read(sandbox.Home);
            Assert.NotNull(progress);
            Assert.Equal(IndexProgress.Unavailable, progress.Outcome);
            Assert.Contains("auto_index_in_background", progress.LastError, StringComparison.Ordinal);
            Assert.False(progress.LooksLive(DateTimeOffset.UtcNow));

            using var check = EngramDatabase.OpenInitialized(sandbox.Home);
            Assert.Null(RepoEnrollment.Get(check, identity)?.LastFullScanAt);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithTheSettingOn_TicksAfterItsStartupLineAppears()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        var root = MakeRepoDir(sandbox, out var identity);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            RepoEnrollment.Enroll(connection, identity, root, now.AddDays(-1));
        }

        Configure(sandbox, autoIndexInBackground: true);

        var log = new Captured();
        var service = new IndexFreshnessService(sandbox.Home, log);

        // StartAsync promises only that ExecuteAsync was handed to the scheduler, not that it has
        // run — D55's documented trap. The real barrier is the loop's own startup line, logged
        // after IndexFreshness.RunAsync actually begins.
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(
                await Settles(() =>
                    log.Lines.Any(line => line.Contains("started", StringComparison.Ordinal))),
                "the freshness service never reported starting");

            using var check = EngramDatabase.OpenInitialized(sandbox.Home);
            Assert.True(
                await Settles(() => RepoEnrollment.Get(check, identity)?.LastFullScanAt is not null),
                "the due repo was never freshened");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
}
