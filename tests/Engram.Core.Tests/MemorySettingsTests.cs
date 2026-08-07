using Engram.Core;

namespace Engram.Core.Tests;

public class MemorySettingsTests
{
    [Theory]
    [InlineData("off", MemoryPrecedence.Off)]
    [InlineData("engram-first", MemoryPrecedence.EngramFirst)]
    [InlineData("engram-only", MemoryPrecedence.EngramOnly)]
    [InlineData("  ENGRAM-Only  ", MemoryPrecedence.EngramOnly)]
    public void Read_KnownValue_IsTakenAsWritten(string text, MemoryPrecedence expected)
    {
        var config = ConfigFile.Parse($"[memory]\nprecedence = \"{text}\"\n");

        var settings = MemorySettings.Read(config);

        Assert.Equal(expected, settings.Precedence);
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void Read_KeyAbsent_IsEngramFirst()
    {
        var settings = MemorySettings.Read(ConfigFile.Parse("[retrieval]\nseed_k = 32\n"));

        Assert.Equal(MemoryPrecedence.EngramFirst, settings.Precedence);
        Assert.Empty(settings.Problems);
    }

    // A value that will not parse must not cost the session its primer, and must not silently
    // become "off" either — that would turn a typo into the one outcome the setting exists to
    // avoid. It falls back to the default and says so where doctor will show it.
    [Fact]
    public void Read_UnknownValue_FallsBackToDefaultAndReportsWhy()
    {
        var config = ConfigFile.Parse("[memory]\nprecedence = \"engram-frist\"\n");

        var settings = MemorySettings.Read(config);

        Assert.Equal(MemorySettings.DefaultPrecedence, settings.Precedence);
        var problem = Assert.Single(settings.Problems);
        Assert.Contains("engram-frist", problem, StringComparison.Ordinal);
    }

    // The shipped config is the one config every install starts from, so a value in it that
    // this type cannot read is a key the user can edit and Engram ignores — which is worse
    // than no key at all, because nothing from outside can tell the difference.
    [Fact]
    public void Read_ShippedDefaultConfig_ParsesAndMatchesTheCodeDefault()
    {
        var settings = MemorySettings.Read(ConfigFile.Parse(DefaultConfig.Content));

        Assert.Empty(settings.Problems);
        Assert.Equal(MemorySettings.DefaultPrecedence, settings.Precedence);
    }

    [Fact]
    public void PrimerLine_Off_SaysNothing() =>
        Assert.Null(MemorySettings.PrimerLine(MemoryPrecedence.Off));

    // Both wordings have to carry the trigger and the verb. The competing instruction fires on
    // the user's literal words and names its own action; a rule that states a ranking without
    // saying what to call loses to it regardless of which is more correct.
    [Theory]
    [InlineData(MemoryPrecedence.EngramFirst)]
    [InlineData(MemoryPrecedence.EngramOnly)]
    public void PrimerLine_On_NamesTheToolAndTheTrigger(MemoryPrecedence precedence)
    {
        var line = MemorySettings.PrimerLine(precedence);

        Assert.NotNull(line);
        Assert.Contains("engram_remember", line, StringComparison.Ordinal);
        Assert.Contains("remember or save", line, StringComparison.Ordinal);
        Assert.Contains("subagent", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MemoryPrecedence.Off)]
    [InlineData(MemoryPrecedence.EngramFirst)]
    [InlineData(MemoryPrecedence.EngramOnly)]
    public void ToText_RoundTripsThroughTryParse(MemoryPrecedence precedence)
    {
        Assert.True(MemorySettings.TryParse(MemorySettings.ToText(precedence), out var parsed));
        Assert.Equal(precedence, parsed);
    }

    [Fact]
    public void Names_CoverEveryValueOfTheEnum() =>
        Assert.Equal(
            Enum.GetValues<MemoryPrecedence>().Select(MemorySettings.ToText).OrderBy(n => n, StringComparer.Ordinal),
            MemorySettings.Names.OrderBy(n => n, StringComparer.Ordinal));
}
