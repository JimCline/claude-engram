using Engram.Core;

namespace Engram.Integration.Tests;

public class EmbeddingBacklogTests
{
    private const int Dimensions = 4;

    private sealed class CountingEmbedder(string model = "test-embedder") : IEmbedder
    {
        public EmbeddingSpace Space { get; } = new(model, Dimensions);

        public int Texts { get; private set; }

        public Task<IReadOnlyList<float[]?>> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            Texts += texts.Count;
            var vectors = new float[]?[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                vectors[i] = [1f, 0f, 0f, 0f];
            }

            return Task.FromResult<IReadOnlyList<float[]?>>(vectors);
        }
    }

    private static EmbeddingSettings Settings(int maxBatch = 16) =>
        EmbeddingSettings.Disabled with { MaxBatch = maxBatch };

    /// <summary>
    /// The contract `remember` makes: the fact is durable immediately, the vector arrives later.
    /// Nothing marks the gap, because the queue is a query over `fact` — so this test is also
    /// the statement that no bookkeeping exists to get out of step.
    /// </summary>
    [Fact]
    public async Task AFactWrittenWithNoEmbedderRunning_IsPickedUpByTheNextPass()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(3);

        var embedder = new CountingEmbedder();
        var backlog = new EmbeddingBacklog(sandbox.Home, embedder, Settings());

        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Embedded);
        Assert.Equal(3, embedder.Texts);
        Assert.Equal(0, VectorIndex.CountPending(sandbox.Connection));
    }

    [Fact]
    public async Task FactsWrittenBetweenPasses_LandOnTheFollowingOne()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFact("first", "written before the first pass");

        var embedder = new CountingEmbedder();
        var backlog = new EmbeddingBacklog(sandbox.Home, embedder, Settings());
        await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        sandbox.AddFact("second", "written after the first pass");
        Assert.Equal(1, VectorIndex.CountPending(sandbox.Connection));

        var second = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Embedded);
        Assert.Equal(2, VectorIndex.Count(sandbox.Connection, liveOnly: true));
    }

    [Fact]
    public async Task ADrainRespectsTheConfiguredBatchSize()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(20);

        // maxBatches is 8 inside a pass, so a batch of 2 leaves work behind and the pass says so
        // rather than running to completion in one wakeup.
        var backlog = new EmbeddingBacklog(sandbox.Home, new CountingEmbedder(), Settings(maxBatch: 2));
        var result = await backlog.DrainOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.BatchLimitReached, result.Outcome);
        Assert.Equal(16, result.Embedded);
        Assert.Equal(4, result.Remaining);
    }

    [Fact]
    public async Task RunAsync_StopsWhenCancelledAndSaysSo()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(2);

        var lines = new List<string>();
        using var stopping = new CancellationTokenSource();
        var backlog = new EmbeddingBacklog(
            sandbox.Home, new CountingEmbedder(), Settings(), lines.Add);

        var run = backlog.RunAsync(stopping.Token);
        while (VectorIndex.CountPending(sandbox.Connection) > 0)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await stopping.CancelAsync();
        await run;

        Assert.Contains(lines, l => l.Contains("started", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Embedded 2", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("stopped", StringComparison.Ordinal));
    }

    /// <summary>
    /// A pass that throws must not take the server down with it — the queue is durable, so the
    /// work survives, and the loop's job is to keep running and say why once.
    /// </summary>
    [Fact]
    public async Task RunAsync_SurvivesAnEmbedderThatThrows()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(1);

        var lines = new List<string>();
        using var stopping = new CancellationTokenSource();
        var backlog = new EmbeddingBacklog(
            sandbox.Home, new ThrowingEmbedder(), Settings(), lines.Add);

        var run = backlog.RunAsync(stopping.Token);
        while (!lines.Any(l => l.Contains("failed", StringComparison.Ordinal)))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await stopping.CancelAsync();
        await run;

        Assert.Contains(lines, l => l.Contains("Embedding pass failed", StringComparison.Ordinal));
        Assert.Equal(1, VectorIndex.CountPending(sandbox.Connection));
    }

    private sealed class ThrowingEmbedder : IEmbedder
    {
        public EmbeddingSpace Space { get; } = new("throwing", Dimensions);

        public Task<IReadOnlyList<float[]?>> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the endpoint is on fire");
    }
}
