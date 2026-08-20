using Engram.Core;

namespace Engram.Integration.Tests;

// D-5: a directive is class-addressed (engram_browse), never content-addressed (recall) — a
// category mismatch, not a tuning problem. This is the one place that exclusion lives
// (RecallRanker.BuildStatementText's "scored" CTE), so it is the one place these tests target.
public class DirectiveRecallExclusionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADirectiveNeverReachesAnyRecallLane_WhileARequiresFactWithTheSameWordsDoes()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        DirectiveFacts.Add(connection, "kestrel loopback binding must always be enforced", T0);
        var requiresResult = FactStore.Remember(
            connection,
            new FactWrite(
                "/facts/kestrel-note", "note", "requires",
                "kestrel loopback binding must always be enforced", "user", "stated"),
            T0.AddSeconds(1));

        var vectorQuery = VectorLane.PrepareQuery(
            connection, sandbox.Home, EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)), "kestrel loopback binding", _ => null);

        var outcome = RecallRanker.Rank(
            connection, "kestrel loopback binding", RetrievalSettings.DefaultBudgetTokens,
            RetrievalSettings.DefaultSeedK, currentSessionId: null, T0.AddSeconds(2), vectorQuery);

        var directiveId = DirectiveFacts.ReadLive(connection).Single().Id;
        var requiresHandle = FactCatalog.HandleFor(requiresResult.FactId);
        var directiveHandle = FactCatalog.HandleFor(directiveId);

        Assert.Contains(outcome.Candidates, c => c.Handle == requiresHandle);
        Assert.DoesNotContain(outcome.Candidates, c => c.Handle == directiveHandle);
    }

    [Fact]
    public void ADirective_DoesNotInflateMatchedOrCorroboratedTotals()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var vectorQueryBefore = VectorLane.PrepareQuery(
            connection, sandbox.Home, EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)), "gopher migration plan", _ => null);
        var before = RecallRanker.Rank(
            connection, "gopher migration plan", RetrievalSettings.DefaultBudgetTokens,
            RetrievalSettings.DefaultSeedK, currentSessionId: null, T0, vectorQueryBefore);

        DirectiveFacts.Add(connection, "gopher migration plan must always be reviewed", T0);

        var vectorQueryAfter = VectorLane.PrepareQuery(
            connection, sandbox.Home, EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)), "gopher migration plan", _ => null);
        var after = RecallRanker.Rank(
            connection, "gopher migration plan", RetrievalSettings.DefaultBudgetTokens,
            RetrievalSettings.DefaultSeedK, currentSessionId: null, T0.AddSeconds(1), vectorQueryAfter);

        Assert.Equal(before.MatchedTotal, after.MatchedTotal);
        Assert.Equal(before.CorroboratedTotal, after.CorroboratedTotal);
    }
}
