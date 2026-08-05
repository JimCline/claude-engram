using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class CannedFactSeederTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Seed_WritesEveryCannedFact()
    {
        using var fixture = new SeedFixture();

        var written = CannedFactSeeder.Seed(fixture.Connection, T0);

        Assert.Equal(CannedFacts.All.Count, written);
        Assert.Equal(CannedFacts.All.Count, FactStore.ReadLive(fixture.Connection).Count);
    }

    // Re-running must be a no-op, not 51 supersessions recording a change nobody made.
    [Fact]
    public void Seed_RunTwice_WritesNothingTheSecondTime()
    {
        using var fixture = new SeedFixture();
        CannedFactSeeder.Seed(fixture.Connection, T0);

        var second = CannedFactSeeder.Seed(fixture.Connection, T0.AddDays(1));

        Assert.Equal(0, second);
        Assert.Equal(CannedFacts.All.Count, FactStore.ReadLive(fixture.Connection).Count);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM supersession;"));
    }

    // A revised body is a real change and must supersede rather than be skipped.
    [Fact]
    public void Seed_WithARevisedBody_SupersedesTheOldStatement()
    {
        using var fixture = new SeedFixture();
        var original = CannedFacts.All[0];
        CannedFactSeeder.Seed(fixture.Connection, [original], T0);

        var revised = original with { Body = "A materially different statement." };
        var written = CannedFactSeeder.Seed(fixture.Connection, [revised], T0.AddDays(1));

        Assert.Equal(1, written);

        var live = Assert.Single(FactStore.ReadLive(fixture.Connection));
        Assert.Equal("A materially different statement.", live.Body);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM supersession;"));
    }

    // The trap a corpus version bump sets. Forgetting a fact leaves nothing live, so a seeder
    // that decides from the live set alone cannot tell "deleted" from "never existed" and
    // writes it straight back. Re-seeding with a REVISED body, which is the only reason to
    // bump the version at all, is the exact path that reaches this.
    [Fact]
    public void Seed_WithARevisedBody_DoesNotResurrectAFactTheUserForgot()
    {
        using var fixture = new SeedFixture();
        var original = CannedFacts.All[0];
        CannedFactSeeder.Seed(fixture.Connection, [original], T0);

        var stored = Assert.Single(FactStore.ReadLive(fixture.Connection));
        FactStore.Forget(fixture.Connection, stored.Id, "user cleared this", T0.AddDays(1));

        var revised = original with { Body = "A materially better version of a deleted fact." };
        var written = CannedFactSeeder.Seed(fixture.Connection, [revised], T0.AddDays(2));

        Assert.Equal(0, written);
        Assert.Empty(FactStore.ReadLive(fixture.Connection));
    }

    // The same protection, without a revision: re-running an unchanged corpus must not
    // undo a forget either.
    [Fact]
    public void Seed_RerunAfterAForget_LeavesTheFactForgotten()
    {
        using var fixture = new SeedFixture();
        CannedFactSeeder.Seed(fixture.Connection, T0);

        var victim = FactStore.ReadLive(fixture.Connection)[0];
        FactStore.Forget(fixture.Connection, victim.Id, "user cleared this", T0.AddDays(1));

        var written = CannedFactSeeder.Seed(fixture.Connection, T0.AddDays(2));

        Assert.Equal(0, written);
        Assert.Equal(CannedFacts.All.Count - 1, FactStore.ReadLive(fixture.Connection).Count);
    }

    // The flip side, so the skip is not simply "never write anything twice": a fact that was
    // never in this store still gets written, even though the corpus has been seeded before.
    [Fact]
    public void Seed_WithANewFact_StillWritesItAfterAnEarlierSeed()
    {
        using var fixture = new SeedFixture();
        var original = CannedFacts.All[0];
        CannedFactSeeder.Seed(fixture.Connection, [original], T0);

        var addition = original with { Subject = "a brand new subject", Body = "A statement never seeded before." };
        var written = CannedFactSeeder.Seed(fixture.Connection, [original, addition], T0.AddDays(1));

        Assert.Equal(1, written);
        Assert.Equal(2, FactStore.ReadLive(fixture.Connection).Count);
    }

    [Fact]
    public void Seed_PlacesFactsUnderTheirTopic()
    {
        using var fixture = new SeedFixture();
        CannedFactSeeder.Seed(fixture.Connection, T0);

        Assert.Equal(
            "/knowledge/claude-code-hooks/subagentstart-envelope",
            fixture.Scalar("SELECT path FROM entity WHERE name = 'subagentstart-envelope';"));
    }

    // The payoff for putting the topic in the path: grouping is a range scan, not a join.
    [Fact]
    public void ReadSubtree_ReturnsEveryFactUnderATopic()
    {
        using var fixture = new SeedFixture();
        CannedFactSeeder.Seed(fixture.Connection, T0);

        var hooks = FactStore.ReadSubtree(fixture.Connection, CannedFactSeeder.TopicPath("claude-code hooks"));

        var expected = CannedFacts.All.Count(f => f.Topic == "claude-code hooks");
        Assert.Equal(expected, hooks.Count);
        Assert.All(hooks, f => Assert.StartsWith("/knowledge/claude-code-hooks/", f.SubjectPath, StringComparison.Ordinal));
    }

    // The bug a naive `LIKE prefix%` produces: a sibling whose name merely starts with the
    // topic's name gets swept into it.
    [Fact]
    public void ReadSubtree_DoesNotLeakSiblingPathsSharingThePrefix()
    {
        using var fixture = new SeedFixture();

        Write(fixture.Connection, "/knowledge/hooks/inside", "belongs here");
        Write(fixture.Connection, "/knowledge/hooks-and-more/outside", "does not belong");
        Write(fixture.Connection, "/knowledge/hooks", "the topic node itself");

        var subtree = FactStore.ReadSubtree(fixture.Connection, "/knowledge/hooks");

        Assert.Equal(2, subtree.Count);
        Assert.DoesNotContain(subtree, f => f.Body == "does not belong");
    }

    // U+FFFD is not the largest encodable character, so a sentinel-based upper bound drops
    // any path containing an astral character. The incremented-prefix bound does not.
    [Fact]
    public void ReadSubtree_KeepsPathsContainingAstralCharacters()
    {
        using var fixture = new SeedFixture();

        Write(fixture.Connection, "/knowledge/hooks/\U0001F600-emoji", "still in the subtree");

        var subtree = FactStore.ReadSubtree(fixture.Connection, "/knowledge/hooks");

        Assert.Equal("still in the subtree", Assert.Single(subtree).Body);
    }

    [Fact]
    public void Seed_MarksEverythingStatedAndNotRegenerable()
    {
        using var fixture = new SeedFixture();
        CannedFactSeeder.Seed(fixture.Connection, T0);

        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM fact WHERE learned_via <> 'stated';"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM fact WHERE regenerable <> 0;"));
    }

    [Fact]
    public void Seed_PreservesTheScopeEachFactWasAuthoredWith()
    {
        using var fixture = new SeedFixture();
        CannedFactSeeder.Seed(fixture.Connection, T0);

        foreach (var scope in CannedFacts.All.Select(f => f.Scope).Distinct())
        {
            Assert.Equal(
                CannedFacts.All.Count(f => f.Scope == scope),
                FactStore.ReadLive(fixture.Connection, scope).Count);
        }
    }

    [Fact]
    public void Seed_MakesEveryFactFindableByLexicalSearch()
    {
        using var fixture = new SeedFixture();
        CannedFactSeeder.Seed(fixture.Connection, T0);

        var hits = FactStore.Search(fixture.Connection, "BEGIN IMMEDIATE", limit: 5);

        Assert.Contains(hits, f => f.SubjectPath.EndsWith("begin-immediate-required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("claude-code hooks", "claude-code-hooks")]
    [InlineData("dotnet and storage", "dotnet-and-storage")]
    [InlineData("Mixed Case Topic", "mixed-case-topic")]
    [InlineData("slashes/and spaces", "slashes-and-spaces")]
    [InlineData("trailing punctuation!!", "trailing-punctuation")]
    public void Slug_ProducesOnePathSegment(string input, string expected)
    {
        var slug = CannedFactSeeder.Slug(input);

        Assert.Equal(expected, slug);
        Assert.False(slug.Contains('/'), "a slug that contains '/' would invent a path level");
    }

    private static void Write(SqliteConnection connection, string path, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "concept", "states", body, "user", "stated"),
            T0);

    private sealed class SeedFixture : IDisposable
    {
        private readonly SandboxHome sandbox = new(initialize: false);

        public SeedFixture()
        {
            Connection = EngramDatabase.OpenInitialized(sandbox.Home);
        }

        public SqliteConnection Connection { get; }

        public object? Scalar(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public void Dispose()
        {
            Connection.Dispose();
            sandbox.Dispose();
        }
    }
}
