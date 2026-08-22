using Engram.Core;

namespace Engram.Core.Tests;

public class ToolProfileSettingsTests
{
    [Fact]
    public void Read_WithNoKeyPresent_ReturnsDefault()
    {
        var settings = ToolProfileSettings.Read(ConfigFile.Empty);

        Assert.Equal(ToolProfile.Default, settings.Profile);
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void Read_WithFull_ReturnsFull()
    {
        var config = ConfigFile.Parse("""
            [mcp]
            tool_profile = "full"
            """);

        var settings = ToolProfileSettings.Read(config);

        Assert.Equal(ToolProfile.Full, settings.Profile);
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void Read_WithAMalformedValue_FallsBackToDefaultAndRecordsAProblem()
    {
        var config = ConfigFile.Parse("""
            [mcp]
            tool_profile = "everything"
            """);

        var settings = ToolProfileSettings.Read(config);

        Assert.Equal(ToolProfile.Default, settings.Profile);
        Assert.Single(settings.Problems);
        Assert.Contains("everything", settings.Problems[0]);
    }

    [Theory]
    [InlineData("default", ToolProfile.Default)]
    [InlineData("full", ToolProfile.Full)]
    public void ToText_RoundTripsThroughTryParse(string text, ToolProfile profile)
    {
        Assert.True(ToolProfileSettings.TryParse(text, out var parsed));
        Assert.Equal(profile, parsed);
        Assert.Equal(text, ToolProfileSettings.ToText(profile));
    }
}
