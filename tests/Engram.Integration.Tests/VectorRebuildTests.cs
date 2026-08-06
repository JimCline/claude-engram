using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// <c>embed --rebuild</c>, against real SQLite and a real <c>vec0</c> table.
/// </summary>
/// <remarks>
/// Tier 2 because every risk here is a property of the stored index rather than of a return
/// value: which table survives, what space it ends up pinned to, and whether a plan that claims
/// to be a dry run leaves the file alone. None of that is reachable without writing a database.
/// </remarks>
public class VectorRebuildTests
{
    private static async Task<VectorSandbox> IndexedAsync(IEmbedder embedder, int facts = 5)
    {
        var sandbox = new VectorSandbox();
        sandbox.AddFacts(facts);
        await VectorBackfill.RunAsync(
            sandbox.Connection, embedder, cancellationToken: TestContext.Current.CancellationToken);
        return sandbox;
    }

    [Fact]
    public void Plan_WithNoIndex_IsABuildThatDiscardsNothing()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = new VectorSandbox();
        sandbox.AddFacts(3);

        var plan = VectorRebuild.Plan(sandbox.Connection, new ScriptedEmbedder().Space);

        Assert.Equal(RebuildAction.Build, plan.Action);
        Assert.Null(plan.Current);
        Assert.Equal(0, plan.Discarded);
        Assert.Equal(3, plan.ToEmbed);
        Assert.Null(plan.Reason);
    }

    [Fact]
    public async Task Plan_WithTheSameSpace_KeepsTheTableAndDropsItsRows()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        var embedder = new ScriptedEmbedder();
        using var sandbox = await IndexedAsync(embedder);

        var plan = VectorRebuild.Plan(sandbox.Connection, embedder.Space);

        Assert.Equal(RebuildAction.Clear, plan.Action);
        Assert.Equal(embedder.Space, plan.Current);
        Assert.Equal(5, plan.Discarded);
        Assert.Equal(5, plan.ToEmbed);
        Assert.Null(plan.Reason);
    }

    [Fact]
    public async Task Plan_WithAWiderModel_MustRecreateTheTableItself()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = await IndexedAsync(new ScriptedEmbedder(dimensions: 4));

        var plan = VectorRebuild.Plan(sandbox.Connection, new ScriptedEmbedder(dimensions: 8).Space);

        Assert.Equal(RebuildAction.Recreate, plan.Action);
        Assert.Contains("4 -> 8", plan.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the whole command exists for.
    /// </summary>
    /// <remarks>
    /// <c>vec0</c> rejects a vector of the wrong width at the row level, so a width change is
    /// self-announcing. It has no opinion whatsoever about a vector of the *right* width from the
    /// wrong model — those store cleanly, rank against each other, and produce distances that
    /// look like ordinary numbers (D18). The pinned model is the only thing that knows, so this
    /// asserts the plan reads it rather than comparing widths and calling it a match.
    /// </remarks>
    [Fact]
    public async Task Plan_WithADifferentModelOfTheSameWidth_StillRecreates()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = await IndexedAsync(new ScriptedEmbedder("first-model", dimensions: 4));

        var plan = VectorRebuild.Plan(sandbox.Connection, new ScriptedEmbedder("second-model", dimensions: 4).Space);

        Assert.Equal(RebuildAction.Recreate, plan.Action);
        Assert.Contains("first-model -> second-model", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_WritesNothing()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        var embedder = new ScriptedEmbedder();
        using var sandbox = await IndexedAsync(embedder);

        var before = VectorIndex.Count(sandbox.Connection);

        // Planned against a space it would have to recreate for, which is the branch with
        // something to destroy.
        VectorRebuild.Plan(sandbox.Connection, new ScriptedEmbedder("other", dimensions: 16).Space);

        Assert.True(VectorIndex.Exists(sandbox.Connection), "planning dropped the table");
        Assert.Equal(before, VectorIndex.Count(sandbox.Connection));
        Assert.Equal(embedder.Space, VectorIndex.ReadSpace(sandbox.Connection));
    }

    [Fact]
    public async Task Run_AfterAModelSwapOfTheSameWidth_RepinsTheIndexToTheNewModel()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = await IndexedAsync(new ScriptedEmbedder("first-model", dimensions: 4));

        var replacement = new ScriptedEmbedder("second-model", dimensions: 4);
        var plan = VectorRebuild.Plan(sandbox.Connection, replacement.Space);
        var result = await VectorRebuild.RunAsync(
            sandbox.Connection, replacement, plan,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(5, result.Embedded);

        // Clearing instead of dropping would leave the old pin in place, and every later backfill
        // pass would then refuse the very embedder this rebuild installed.
        Assert.Equal(replacement.Space, VectorIndex.ReadSpace(sandbox.Connection));
        Assert.Equal(0, VectorIndex.CountPending(sandbox.Connection));

        var next = await VectorBackfill.RunAsync(
            sandbox.Connection, replacement, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BackfillOutcome.Completed, next.Outcome);
    }

    [Fact]
    public async Task Run_AfterAWidthChange_ReplacesEveryVector()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = await IndexedAsync(new ScriptedEmbedder(dimensions: 4));

        var wider = new ScriptedEmbedder("wide-model", dimensions: 8);
        var plan = VectorRebuild.Plan(sandbox.Connection, wider.Space);
        var result = await VectorRebuild.RunAsync(
            sandbox.Connection, wider, plan,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(5, VectorIndex.Count(sandbox.Connection));
        Assert.Equal(8, VectorIndex.ReadSpace(sandbox.Connection)?.Dimensions);

        // Searchable at the new width, which a stale table would reject outright.
        var query = new float[8];
        query[0] = 1f;
        Assert.NotEmpty(VectorIndex.Search(sandbox.Connection, query, k: 3));
    }

    [Fact]
    public async Task Run_DoesNotReviveFactsThatWereClosedSinceTheIndexWasBuilt()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        var embedder = new ScriptedEmbedder();
        using var sandbox = await IndexedAsync(embedder);

        var closed = sandbox.AddFact("doomed", "this one gets retired");
        sandbox.Close(closed);

        var plan = VectorRebuild.Plan(sandbox.Connection, embedder.Space);
        var result = await VectorRebuild.RunAsync(
            sandbox.Connection, embedder, plan,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackfillOutcome.Completed, result.Outcome);
        Assert.Equal(5, plan.ToEmbed);
        Assert.Equal(5, VectorIndex.Count(sandbox.Connection, liveOnly: true));
    }

    [Fact]
    public async Task Run_WithATextTheEmbedderRefuses_LeavesItPendingRatherThanPlaceholding()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        using var sandbox = await IndexedAsync(new ScriptedEmbedder());

        var picky = new ScriptedEmbedder(fails: text => text.Contains("number 2", StringComparison.Ordinal));
        var plan = VectorRebuild.Plan(sandbox.Connection, picky.Space);
        var result = await VectorRebuild.RunAsync(
            sandbox.Connection, picky, plan,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Embedded);

        // Stalled rather than completed, and that is the whole design: the queue is a query, so a
        // refused text stays in it, and the next pass reads the same fact and fails the same way.
        // Backfill notices a batch that wrote nothing and stops instead of spinning.
        Assert.Equal(BackfillOutcome.StalledOnFailures, result.Outcome);

        // Two, for one fact — it was attempted once in the pass that embedded the other four and
        // once more in the pass that detected the stall. Failures are attempts, not facts.
        Assert.Equal(2, result.Failed);

        // A placeholder row would have emptied the queue and then answered every query at NaN
        // distance. No row means the fact is simply still waiting.
        Assert.Equal(1, VectorIndex.CountPending(sandbox.Connection));
        Assert.Equal(4, VectorIndex.Count(sandbox.Connection));
    }

    [Fact]
    public async Task Run_ReportsProgressAsCumulativeTotals()
    {
        Assert.SkipUnless(VectorExtensionFile.Path is not null, VectorExtensionFile.SkipReason);

        var embedder = new ScriptedEmbedder();
        using var sandbox = await IndexedAsync(embedder, facts: 6);

        var seen = new List<int>();
        var plan = VectorRebuild.Plan(sandbox.Connection, embedder.Space);
        await VectorRebuild.RunAsync(
            sandbox.Connection, embedder, plan, batchSize: 2,
            progress: pass => seen.Add(pass.Embedded),
            cancellationToken: TestContext.Current.CancellationToken);

        // Reported per batch and never going backwards — a per-pass number would read 2, 2, 2 and
        // look like a stall on a store where this takes minutes.
        Assert.NotEmpty(seen);
        Assert.Equal(seen.OrderBy(count => count).ToList(), seen);
        Assert.Equal(6, seen[^1]);
    }
}
