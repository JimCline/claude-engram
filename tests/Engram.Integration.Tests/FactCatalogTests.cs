using Engram.Core;

namespace Engram.Integration.Tests;

public class FactCatalogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadLongTerm_OnAnInitializedHome_ReturnsTheWholeSeededCorpus()
    {
        using var sandbox = new SandboxHome(initialize: false);
        EngramInitializer.Initialize(sandbox.Home);

        var catalog = FactCatalog.ReadLongTerm(sandbox.Home, T0);

        Assert.Equal(CannedFacts.All.Count, catalog.Count);
    }

    // The corpus reaching recall through the store must say the same things it said as a
    // hardcoded list, or this was a regression dressed as a refactor.
    [Fact]
    public void ReadLongTerm_PreservesEveryAuthoredBody()
    {
        using var sandbox = new SandboxHome(initialize: false);
        EngramInitializer.Initialize(sandbox.Home);

        var bodies = FactCatalog.ReadLongTerm(sandbox.Home, T0).Select(f => f.Body).ToHashSet(StringComparer.Ordinal);

        Assert.All(CannedFacts.All, fact => Assert.Contains(fact.Body, bodies));
    }

    [Fact]
    public void ReadLongTerm_PreservesScopeAndEvidence()
    {
        using var sandbox = new SandboxHome(initialize: false);
        EngramInitializer.Initialize(sandbox.Home);
        var expected = CannedFacts.All[0];

        var actual = Assert.Single(
            FactCatalog.ReadLongTerm(sandbox.Home, T0),
            f => f.Body == expected.Body);

        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.Evidence, actual.Evidence);
        Assert.Equal(expected.Predicate, actual.Predicate);
        Assert.Equal(expected.Subject, actual.Subject);
    }

    [Fact]
    public void ReadLongTerm_StillRanksThroughTheExistingRecallPath()
    {
        using var sandbox = new SandboxHome(initialize: false);
        EngramInitializer.Initialize(sandbox.Home);

        var catalog = FactCatalog.ReadLongTerm(sandbox.Home, T0);
        var ranked = RecallEngine.Rank("BEGIN IMMEDIATE transaction", catalog);

        Assert.Contains(ranked, r => r.Fact.Body.Contains("BEGIN IMMEDIATE", StringComparison.Ordinal));
    }

    [Fact]
    public void Initialize_RunTwice_DoesNotDuplicateOrSupersedeTheCorpus()
    {
        using var sandbox = new SandboxHome(initialize: false);

        EngramInitializer.Initialize(sandbox.Home);
        EngramInitializer.Initialize(sandbox.Home);

        Assert.Equal(CannedFacts.All.Count, FactCatalog.ReadLongTerm(sandbox.Home, T0).Count);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM supersession;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    // The case row-counting would get wrong: a user who forgot everything must not have it
    // handed back to them on the next init.
    [Fact]
    public void Initialize_AfterEverythingWasForgotten_DoesNotResurrectTheCorpus()
    {
        using var sandbox = new SandboxHome(initialize: false);
        EngramInitializer.Initialize(sandbox.Home);

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            foreach (var fact in FactStore.ReadLive(connection))
            {
                FactStore.Forget(connection, fact.Id, "user cleared memory", T0);
            }
        }

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Empty(FactCatalog.ReadLongTerm(sandbox.Home, T0));
    }

    [Fact]
    public void Initialize_ReportsTheDatabaseAsCreatedOnlyTheFirstTime()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var first = EngramInitializer.Initialize(sandbox.Home);
        var second = EngramInitializer.Initialize(sandbox.Home);

        Assert.True(first.Single(p => p.Path == sandbox.Home.DatabasePath).Created);
        Assert.False(second.Single(p => p.Path == sandbox.Home.DatabasePath).Created);
    }

    // Every authored topic has to survive the round trip as written. Comparing the whole set
    // rather than one example, so a slug leaking through for any single topic fails.
    [Fact]
    public void ReadLongTerm_PreservesEveryTopicAsAuthored()
    {
        using var sandbox = new SandboxHome(initialize: false);
        EngramInitializer.Initialize(sandbox.Home);

        var actual = FactCatalog.ReadLongTerm(sandbox.Home, T0).Select(f => f.Topic).ToHashSet(StringComparer.Ordinal);
        var expected = CannedFacts.All.Select(f => f.Topic).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    // Topic nodes are addressing metadata, not belief content: creating them must not put a
    // fact in the store, or `repair` and the append-only rule are both being lied to.
    [Fact]
    public void EnsureTopics_WritesNoFactsAndIsIdempotent()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        CannedFactSeeder.EnsureTopics(connection, T0);
        CannedFactSeeder.EnsureTopics(connection, T0.AddDays(1));

        Assert.Empty(FactStore.ReadLive(connection));

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entity WHERE kind = 'topic';";
        Assert.Equal(
            (long)CannedFacts.All.Select(f => f.Topic).Distinct(StringComparer.Ordinal).Count(),
            command.ExecuteScalar());
    }

    // The second segment, whatever the root. This used to special-case /knowledge and call
    // everything else "memory", which stopped being right the moment user captures got a
    // root of their own: their topic would have read as "memory" in the primer with nothing
    // to report the loss.
    [Theory]
    [InlineData("/knowledge/claude-code-hooks/subagentstart-envelope", "claude-code-hooks")]
    [InlineData("/knowledge/this-project", "this-project")]
    [InlineData("/user/about-you/ab12cd34", "about-you")]
    [InlineData("/people/jim", "jim")]
    [InlineData("/orphan", "memory")]
    public void TopicOf_ReadsTheTopicSegment(string path, string expected) =>
        Assert.Equal(expected, FactCatalog.TopicOf(path));

    [Fact]
    public void ToCannedFact_ReportsAgeFromWhenTheFactWasWritten()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FactStore.Remember(
            connection,
            new FactWrite("/people/jim", "person", "prefers", "Tabs.", "user", "stated"),
            T0);

        var fact = FactCatalog.ToCannedFact(Assert.Single(FactStore.ReadLive(connection)), T0.AddDays(9));

        Assert.Equal(9, fact.AgeDays);
    }
}
