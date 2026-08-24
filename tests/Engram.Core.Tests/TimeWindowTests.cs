namespace Engram.Core.Tests;

public class TimeWindowTests
{
    [Theory]
    [InlineData("10", 10)]
    [InlineData("10s", 10)]
    [InlineData("5m", 300)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void TryParse_ValidGrammar_ParsesToExpectedSeconds(string value, int expectedSeconds)
    {
        Assert.True(TimeWindow.TryParse(value, out var window));
        Assert.Equal(expectedSeconds, (int)window.TotalSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10x")]
    [InlineData("1.5m")]
    [InlineData("d")]
    public void TryParse_InvalidGrammar_ReturnsFalse(string value)
    {
        Assert.False(TimeWindow.TryParse(value, out _));
    }

    [Fact]
    public void TryParse_ALargeDayCount_ComputesWithoutIntOverflow()
    {
        // 50_000 * 86_400 overflows Int32 (~24,855d wraps around); done in long it does not.
        Assert.True(TimeWindow.TryParse("50000d", out var window));
        Assert.Equal(50_000L * 86_400, (long)window.TotalSeconds);
    }
}
