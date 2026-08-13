using System.Diagnostics;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. The two kinds of work that have a duration — indexing a repo and draining the embedding
/// backlog — recorded nothing at all, so nothing outside Engram could say they were happening.
/// What matters here is that each says both when it starts and when it stops: without the second
/// half, anything reporting activity has to guess how long to keep saying it, and a guess about
/// how long a repository takes is not a design.
/// </summary>
public class ActivityEventsTests
{
    private static IReadOnlyList<TelemetryRecord> Events(EngramHome home, string kind)
    {
        var path = Telemetry.ResolvePath(home);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(Telemetry.TryParse)
            .Where(record => record is not null && record.Kind == kind)
            .Select(record => record!)
            .ToList();
    }

    private static async Task<bool> Settles(Func<bool> condition, int seconds = 40)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
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

    [Fact]
    public void AnIndexRun_RecordsThatItStartedAndThatItFinished()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(Path.GetTempPath(), $"engram-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Thing.cs"), "public class Thing { public int N; }\n");

        try
        {
            var exit = IndexCommand.Run(
                sandbox.Home.Root, [repo], TextWriter.Null, TextWriter.Null);

            Assert.Equal(0, exit);

            var phases = Events(sandbox.Home, TelemetryEventKind.Index).Select(e => e.Phase).ToList();

            Assert.Equal(["started", "finished"], phases);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    /// <summary>
    /// The index event carries no counts. <c>fact_count</c> on a recall means facts returned to the
    /// model, and putting a different number of a different thing into a nearby field is exactly
    /// what D43 traced a wrong conclusion back to. The report on stdout says what was written.
    /// </summary>
    [Fact]
    public void AnIndexEvent_CarriesNoBorrowedCounts()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(Path.GetTempPath(), $"engram-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Thing.cs"), "public class Thing { }\n");

        try
        {
            IndexCommand.Run(sandbox.Home.Root, [repo], TextWriter.Null, TextWriter.Null);

            foreach (var record in Events(sandbox.Home, TelemetryEventKind.Index))
            {
                Assert.Null(record.FactCount);
                Assert.Null(record.TokensReturned);
                Assert.Null(record.LongTermFactCount);
            }
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private sealed class OneVectorEmbedder : IEmbedder
    {
        public EmbeddingSpace Space { get; } = new("test-embedder", 4);

        public Task<IReadOnlyList<float[]?>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            var vectors = new float[]?[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                vectors[i] = [1f, 0f, 0f, 0f];
            }

            return Task.FromResult<IReadOnlyList<float[]?>>(vectors);
        }
    }

    /// <summary>
    /// Transitions, not samples: one <c>started</c> for a backfill rather than one per batch. A
    /// full backfill here is hundreds of batches, and putting each into the log would change what
    /// telemetry.jsonl is — it is the file D18 and D43 read to answer how memory is used. How far
    /// along the work is lives in embedding.json, which is maintained for that question already.
    /// </summary>
    [Fact]
    public async Task TheEmbeddingBacklog_RecordsOneStartAndOneFinishPerBackfill()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        // More work than one pass can take. A pass is eight batches, so at MaxBatch 2 this spans
        // three of them — which is the whole point: with a single-pass backfill, "once per
        // transition" and "once per pass" produce identical output and the assertion below proves
        // nothing. Measured — at 12 facts and MaxBatch 4 this test passed with the guard deleted.
        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(40);

        using var cts = new CancellationTokenSource();
        var backlog = new EmbeddingBacklog(
            sandbox.Home, new OneVectorEmbedder(), EmbeddingSettings.Disabled with { MaxBatch = 2 });

        var run = backlog.RunAsync(cts.Token);

        var arrived = await Settles(() =>
        {
            var phases = Events(sandbox.Home, TelemetryEventKind.Embedding).Select(e => e.Phase).ToList();
            return phases.Contains("started") && phases.Contains("finished");
        });

        await cts.CancelAsync();
        await run;

        Assert.True(arrived, "the backlog never reported starting and finishing");

        var phases = Events(sandbox.Home, TelemetryEventKind.Embedding).Select(e => e.Phase).ToList();

        Assert.Equal(1, phases.Count(p => p == "started"));
        Assert.Equal(1, phases.Count(p => p == "finished"));
        Assert.Equal("started", phases[0]);
    }

    /// <summary>
    /// A backlog that never had anything to do says nothing. Otherwise every idle server would
    /// announce a backfill that did not happen, and a reader cannot tell that from one that did.
    /// </summary>
    [Fact]
    public async Task AnIdleBacklog_RecordsNothing()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();

        using var cts = new CancellationTokenSource();
        var backlog = new EmbeddingBacklog(
            sandbox.Home, new OneVectorEmbedder(), EmbeddingSettings.Disabled with { MaxBatch = 4 });

        var run = backlog.RunAsync(cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await run;

        Assert.Empty(Events(sandbox.Home, TelemetryEventKind.Embedding));
    }

    /// <summary>
    /// TelemetryEventKind.Enrollment is declared and listed in TelemetryEventKind.All but had no
    /// direct test anywhere in the tree — the class of gap D56 named for other kinds, where a
    /// declared-but-unverified kind reads as switched off.
    /// </summary>
    [Fact]
    public void AnEnrollmentDecision_RecordsRepoAndDecision()
    {
        using var sandbox = new SandboxHome();
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        var exit = RepoCommand.Run(sandbox.Home.Root, ["enroll", root], TextWriter.Null, TextWriter.Null);
        Assert.Equal(0, exit);

        // Read the identity back from what was actually enrolled rather than recomputing it
        // independently — CodeIndexer.ResolveIdentity(root) can disagree with the enrolled
        // identity here because the enroll path resolves the checkout root (and so its
        // identity) from git's own canonicalized toplevel, not from this test's raw path.
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var enrolled = Assert.Single(RepoEnrollment.ListAll(connection));

        var record = Assert.Single(Events(sandbox.Home, TelemetryEventKind.Enrollment));
        Assert.Equal(enrolled.Identity, record.Repo);
        Assert.Equal("enroll", record.Decision);
    }

    private static bool GitInit(string directory)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("init");
            info.ArgumentList.Add("-q");

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
