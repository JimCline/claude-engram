using Engram.Core;

namespace Engram.Core.Tests;

public class BackupSettingsTests
{
    private static BackupSettings Read(string toml) => BackupSettings.Read(ConfigFile.Parse(toml));

    /// <remarks>
    /// Compared field by field rather than by record equality: <c>Problems</c> is a collection,
    /// and a record compares one by reference, so two settings agreeing on every value would be
    /// unequal purely because one holds an array and the other a list.
    /// </remarks>
    private static void AssertIsDefault(BackupSettings settings)
    {
        Assert.Equal(BackupSettings.DefaultEnabled, settings.Enabled);
        Assert.Equal(BackupSettings.DefaultJournal, settings.Journal);
        Assert.Equal(BackupSettings.DefaultIntervalMinutes, settings.IntervalMinutes);
        Assert.Equal(BackupSettings.DefaultKeepHourly, settings.KeepHourly);
        Assert.Equal(BackupSettings.DefaultKeepDaily, settings.KeepDaily);
        Assert.Equal(BackupSettings.DefaultKeepWeekly, settings.KeepWeekly);
    }

    [Fact]
    public void AnAbsentSection_LeavesEveryDefaultInPlace()
    {
        var settings = Read("[retrieval]\nseed_k = 4\n");

        AssertIsDefault(settings);
        Assert.Empty(settings.Problems);
    }

    /// <summary>
    /// The config Engram writes on init has to mean what the compiled defaults mean. A template
    /// that drifts from them turns "I did not change anything" into a silent change.
    /// </summary>
    [Fact]
    public void TheShippedDefaultConfig_ParsesToTheCompiledDefaults()
    {
        var settings = BackupSettings.Read(ConfigFile.Parse(DefaultConfig.Content));

        AssertIsDefault(settings);
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void ExplicitValues_AreRead()
    {
        var settings = Read("[backup]\nenabled = false\ninterval_minutes = 15\nkeep_hourly = 3\nkeep_daily = 2\nkeep_weekly = 1\n");

        Assert.False(settings.Enabled);
        Assert.Equal(15, settings.IntervalMinutes);
        Assert.Equal(3, settings.KeepHourly);
        Assert.Equal(2, settings.KeepDaily);
        Assert.Equal(1, settings.KeepWeekly);
        Assert.Empty(settings.Problems);
    }

    /// <summary>
    /// A retention count of nought reads like "keep none". Honouring it would let one mistyped
    /// line delete every snapshot, which is the failure the whole feature exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("keep_hourly")]
    [InlineData("keep_daily")]
    [InlineData("keep_weekly")]
    public void AZeroRetentionCount_IsRefusedAndReported(string key)
    {
        var settings = Read($"[backup]\n{key} = 0\n");

        AssertIsDefault(settings);
        Assert.Contains(settings.Problems, p => p.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void ANegativeInterval_IsRefusedAndReported()
    {
        var settings = Read("[backup]\ninterval_minutes = -5\n");

        Assert.Equal(BackupSettings.DefaultIntervalMinutes, settings.IntervalMinutes);
        Assert.Single(settings.Problems);
    }
}
