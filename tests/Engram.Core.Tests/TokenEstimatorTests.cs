using Engram.Core;

namespace Engram.Core.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void Estimate_EmptyString_ReturnsZero()
    {
        Assert.Equal(0, TokenEstimator.Estimate(string.Empty));
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("abcd", 2)]
    [InlineData("abcdefg", 2)]
    [InlineData("1234567890", 3)]
    public void Estimate_RoundsUpCharacterCountDividedByRatio(string text, int expected)
    {
        Assert.Equal(expected, TokenEstimator.Estimate(text));
    }

    [Fact]
    public void Estimate_MatchesCeilingOfLengthOverRatio()
    {
        var text = new string('x', 214);
        var expected = (int)Math.Ceiling(214 / 3.6);

        Assert.Equal(expected, TokenEstimator.Estimate(text));
    }
}
