using Engram.Core;

namespace Engram.Core.Tests;

public class FactDensityTests
{
    [Fact]
    public void NoSessionsHaveWritten_ReportsNothingRatherThanAZeroMedian()
    {
        var stat = FactDensity.Summarize([]);

        Assert.Equal(0, stat.Sessions);
        Assert.Equal(0, stat.Facts);
        Assert.False(stat.MeetsGate);
    }

    [Fact]
    public void OddCount_TakesTheMiddleValue()
    {
        var stat = FactDensity.Summarize([9, 1, 4]);

        Assert.Equal(4, stat.Median);
        Assert.Equal(1, stat.Min);
        Assert.Equal(9, stat.Max);
        Assert.Equal(14, stat.Facts);
        Assert.Equal(3, stat.Sessions);
    }

    [Fact]
    public void EvenCount_AveragesTheTwoMiddleValues()
    {
        var stat = FactDensity.Summarize([1, 4, 6, 20]);

        Assert.Equal(5, stat.Median);
    }

    // D16 says "roughly five" and lapses "below" it. The boundary is the only value where the
    // wording and the number could disagree, so it is pinned rather than left to the reader.
    [Theory]
    [InlineData(new[] { 5, 5, 5 }, true)]
    [InlineData(new[] { 4, 5 }, false)]
    [InlineData(new[] { 4, 4, 4 }, false)]
    [InlineData(new[] { 5, 6 }, true)]
    public void TheGateIsAtOrAboveFive(int[] counts, bool expected)
    {
        Assert.Equal(expected, FactDensity.Summarize(counts).MeetsGate);
        Assert.Equal(FactDensity.Gate, FactDensity.Summarize(counts).Gate);
    }
}
