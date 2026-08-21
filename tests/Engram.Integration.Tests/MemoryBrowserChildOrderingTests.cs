using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Falsification arm 3 of the single-pass accumulation in <see cref="MemoryBrowser.Browse"/>
/// (docs/memory-expansion/05b-browse-depth-bound-spec.md): the ordinal tiebreak on child
/// ordering. Driven directly against <see cref="MemoryBrowser.OrderChildren"/> with an
/// already-scrambled in-memory sequence, bypassing the SQL round-trip entirely — SQLite's
/// GROUP BY on <c>e.path</c> always returns rows path-ascending, which for siblings of one
/// parent is provably identical to display-name-ordinal order regardless of write order, so no
/// fixture built through <see cref="MemoryBrowser.Browse"/> can exercise this tiebreak at all.
/// </summary>
public class MemoryBrowserChildOrderingTests
{
    [Fact]
    public void OrderChildren_TiedTotals_OrdersByDisplayNameAscendingRegardlessOfInputOrder()
    {
        var scrambled = new[] { "c17", "c03", "c19", "c00", "c11", "c08" }
            .Select(name => (Display: name, ChildPath: $"/many/{name}", Total: 1));

        var ordered = MemoryBrowser.OrderChildren(scrambled);

        Assert.Equal(
            new[] { "c00", "c03", "c08", "c11", "c17", "c19" },
            ordered.Select(entry => entry.Display));
    }

    [Fact]
    public void OrderChildren_MixedTotals_RanksByTotalFirstThenNameOnTies()
    {
        var entries = new[]
        {
            (Display: "low-b", ChildPath: "/x/low-b", Total: 1),
            (Display: "high", ChildPath: "/x/high", Total: 5),
            (Display: "low-a", ChildPath: "/x/low-a", Total: 1),
        };

        var ordered = MemoryBrowser.OrderChildren(entries);

        Assert.Equal(
            new[] { "high", "low-a", "low-b" },
            ordered.Select(entry => entry.Display));
    }
}
