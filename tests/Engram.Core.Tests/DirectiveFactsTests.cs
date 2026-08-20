using Engram.Core;

namespace Engram.Core.Tests;

public class DirectiveFactsTests
{
    [Fact]
    public void PathForTheSameStatementIsStable()
    {
        var statement = "always use BEGIN IMMEDIATE for writes";

        Assert.Equal(DirectiveFacts.PathFor(statement), DirectiveFacts.PathFor(statement));
    }

    [Fact]
    public void PathForDistinctStatementsIsDistinct()
    {
        var a = DirectiveFacts.PathFor("always use BEGIN IMMEDIATE for writes");
        var b = DirectiveFacts.PathFor("never commit directly to main in this repo");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void PathForStartsWithTheRoot()
    {
        var path = DirectiveFacts.PathFor("always use BEGIN IMMEDIATE for writes");

        Assert.StartsWith(DirectiveFacts.Root + "/", path, StringComparison.Ordinal);
    }

    // A slug this long would otherwise make the path unbounded — truncation keeps it readable
    // for engram_browse without touching identity, which lives in the fingerprint suffix.
    [Fact]
    public void PathForALongStatementStaysBounded()
    {
        var statement = string.Join(
            " ", Enumerable.Range(0, 200).Select(i => $"word{i}"));

        var path = DirectiveFacts.PathFor(statement);

        Assert.True(path.Length < 200, $"expected a bounded path, got {path.Length} chars: {path}");
    }

    // Two statements differing only past the truncation boundary must still get distinct
    // paths — proving identity really lives in the fingerprint suffix, not the truncated slug.
    [Fact]
    public void PathForTwoLongStatementsSharingAPrefixStaysDistinct()
    {
        var prefix = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"word{i}"));

        var a = DirectiveFacts.PathFor(prefix + " alpha");
        var b = DirectiveFacts.PathFor(prefix + " beta");

        Assert.NotEqual(a, b);
    }
}
