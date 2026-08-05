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
        private readonly SandboxHome sandbox = new();

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
