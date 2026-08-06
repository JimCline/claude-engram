using Engram.Core;

namespace Engram.Integration.Tests;

public class VectorBackfillTests
{
    private const int Dimensions = 4;

    /// <summary>
    /// Deterministic, and able to fail the way a real provider fails: per text, not per batch.
    /// </summary>
    private sealed class ScriptedEmbedder(
        string model = "test-embedder",
        int dimensions = Dimensions,
        Func<string, bool>? fails = null,
        int? actualWidth = null) : IEmbedder
    {
        public EmbeddingSpace Space { get; } = new(model, dimensions);

        public int Calls { get; private set; }

        public Task<IReadOnlyList<float[]?>> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var width = actualWidth ?? Space.Dimensions;
            var vectors = new float[]?[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                if (fails?.Invoke(texts[i]) == true)
                {
                    continue;
                }

                var vector = new float[width];
                vector[0] = 1f;
                vector[Math.Abs(texts[i].GetHashCode(StringComparison.Ordinal)) % width] += 0.5f;
                vectors[i] = vector;
            }

            return Task.FromResult<IReadOnlyList<float[]?>>(vectors);
        }
    }

    [Fact]
    public async Task Run_EmbedsEverythingPendingAndCreatesTheIndex()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(5);

        var embedder = new ScriptedEmbedder();
        var result = await VectorBackfill.RunAsync(
            sandbox.Connection, embedder, batchSize: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(5, result.Embedded);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Remaining);
        Assert.Equal(5, VectorIndex.Count(sandbox.Connection, liveOnly: true));
        Assert.Equal(0, VectorIndex.CountPending(sandbox.Connection));
        Assert.Equal(embedder.Space, VectorIndex.ReadSpace(sandbox.Connection));
    }

    [Fact]
    public async Task Run_WithNothingPending_CostsNoEmbedderCall()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();

        var embedder = new ScriptedEmbedder();
        var result = await VectorBackfill.RunAsync(
            sandbox.Connection, embedder, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(0, embedder.Calls);
    }

    [Fact]
    public async Task Run_IsResumable()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(5);

        var embedder = new ScriptedEmbedder();
        var first = await VectorBackfill.RunAsync(
            sandbox.Connection, embedder, batchSize: 2, maxBatches: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.BatchLimitReached, first.Outcome);
        Assert.Equal(2, first.Embedded);
        Assert.Equal(3, first.Remaining);

        var second = await VectorBackfill.RunAsync(
            sandbox.Connection, embedder, batchSize: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, second.Outcome);
        Assert.Equal(3, second.Embedded);
        Assert.Equal(0, VectorIndex.CountPending(sandbox.Connection));
    }

    /// <summary>
    /// A text the provider cannot handle must not block the facts batched with it, and must not
    /// leave a placeholder row — the queue is the absence of a vector, so no row means retried.
    /// </summary>
    [Fact]
    public async Task Run_WithOnePoisonText_EmbedsTheRestAndLeavesItPending()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(4);
        var poisonId = sandbox.AddFact("poison", "this one cannot be embedded");

        var embedder = new ScriptedEmbedder(fails: text => text.Contains("cannot", StringComparison.Ordinal));
        var result = await VectorBackfill.RunAsync(
            sandbox.Connection, embedder, batchSize: 10, maxBatches: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Embedded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Remaining);
        Assert.Equal(4, VectorIndex.Count(sandbox.Connection));

        var stillPending = Assert.Single(VectorIndex.ReadBackfillBatch(sandbox.Connection, 10));
        Assert.Equal(poisonId, stillPending.FactId);
    }

    /// <summary>
    /// The queue is ordered, so a batch where every text fails would be re-read unchanged. The
    /// loop has to notice rather than spin, burning one provider call per turn.
    /// </summary>
    [Fact]
    public async Task Run_WhenAWholeBatchFails_StopsInsteadOfSpinning()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(3);

        var embedder = new ScriptedEmbedder(fails: _ => true);
        var result = await VectorBackfill.RunAsync(
            sandbox.Connection, embedder, batchSize: 10, maxBatches: 100,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.StalledOnFailures, result.Outcome);
        Assert.Equal(0, result.Embedded);
        Assert.Equal(3, result.Failed);
        Assert.Equal(3, result.Remaining);
        Assert.Equal(1, embedder.Calls);
        Assert.Equal(0, VectorIndex.Count(sandbox.Connection));
    }

    [Fact]
    public async Task Run_AgainstAnIndexFromAnotherModel_RefusesAndWritesNothing()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(2);

        await VectorBackfill.RunAsync(
            sandbox.Connection, new ScriptedEmbedder("first-model"),
            cancellationToken: TestContext.Current.CancellationToken);
        sandbox.AddFact("later", "written after the first pass");

        // Same width, different model — the case vec0 itself cannot catch, and the reason the
        // model is recorded at all.
        var swapped = new ScriptedEmbedder("second-model");
        var result = await VectorBackfill.RunAsync(
            sandbox.Connection, swapped, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.SpaceMismatch, result.Outcome);
        Assert.Equal(0, result.Embedded);
        Assert.Equal(1, result.Remaining);
        Assert.Equal(0, swapped.Calls);
        Assert.Equal(new EmbeddingSpace("first-model", Dimensions), VectorIndex.ReadSpace(sandbox.Connection));
        Assert.Equal(2, VectorIndex.Count(sandbox.Connection));
    }

    [Fact]
    public async Task Run_AfterDrop_RebuildsIntoTheNewSpace()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(3);

        await VectorBackfill.RunAsync(
            sandbox.Connection, new ScriptedEmbedder("first-model"),
            cancellationToken: TestContext.Current.CancellationToken);

        VectorIndex.Drop(sandbox.Connection);

        var wider = new ScriptedEmbedder("second-model", dimensions: 8);
        var result = await VectorBackfill.RunAsync(
            sandbox.Connection, wider, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Embedded);
        Assert.Equal(wider.Space, VectorIndex.ReadSpace(sandbox.Connection));
    }

    /// <summary>
    /// A provider that declares one width and returns another corrupts every later query without
    /// erroring anywhere, so the mismatch is caught at the boundary rather than trusted.
    /// </summary>
    [Fact]
    public async Task Run_WithAnEmbedderThatLiesAboutItsWidth_Fails()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(1);

        var liar = new ScriptedEmbedder(dimensions: Dimensions, actualWidth: Dimensions * 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => VectorBackfill.RunAsync(
            sandbox.Connection, liar, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, VectorIndex.Count(sandbox.Connection));
    }

    /// <summary>
    /// Nothing retires a vector when its fact is superseded, so a pass that only filled would
    /// leave retracted beliefs ranking forever.
    /// </summary>
    [Fact]
    public async Task Run_RetiresVectorsSupersededSinceTheLastPass()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        var retracted = sandbox.AddFact("a", "believed for now");
        sandbox.AddFact("b", "believed throughout");

        await VectorBackfill.RunAsync(
            sandbox.Connection, new ScriptedEmbedder(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, VectorIndex.Count(sandbox.Connection, liveOnly: true));

        sandbox.Close(retracted);

        await VectorBackfill.RunAsync(
            sandbox.Connection, new ScriptedEmbedder(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, VectorIndex.Count(sandbox.Connection));
        Assert.Equal(1, VectorIndex.Count(sandbox.Connection, liveOnly: true));
    }

    [Fact]
    public async Task Run_SkipsFactsClosedBeforeItStarted()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.Close(sandbox.AddFact("gone", "this belief was retracted"));
        sandbox.AddFact("live", "this belief still holds");

        var result = await VectorBackfill.RunAsync(
            sandbox.Connection, new ScriptedEmbedder(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Embedded);
        Assert.Equal(1, VectorIndex.Count(sandbox.Connection));
    }
}
