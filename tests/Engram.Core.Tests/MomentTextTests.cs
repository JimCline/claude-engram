using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// Tier 1. Two defects lived in one line for the life of the read path, and each needs its own
/// guard: the time was discarded, and what remained was the UTC day rather than the reader's.
/// </summary>
public class MomentTextTests
{
    private static readonly TimeZoneInfo Pacific =
        TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private static long At(string iso) =>
        DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds();

    /// <summary>
    /// The one that was silently wrong in shipped output. 01:30 UTC is the previous evening in
    /// Pacific, so a UTC render dates every fact saved after mid-afternoon to tomorrow — and the
    /// agent comparing it against a locally-stated "today" has no way to notice.
    /// </summary>
    [Fact]
    public void AnEveningInstantWestOfGreenwich_ShowsTheReadersDayNotTheUtcOne()
    {
        var instant = At("2026-08-07T01:30:00Z");

        Assert.Equal("2026-08-06 18:30", MomentText.In(instant, Pacific));
    }

    /// <summary>
    /// Facts store <c>valid_from</c> to the second. Rendering a bare date discarded it, leaving
    /// every fact written in one working session mutually unordered on screen.
    /// </summary>
    [Fact]
    public void TheTimeOfDay_SurvivesTheRender()
    {
        var morning = At("2026-08-06T09:05:00Z");
        var evening = At("2026-08-06T21:47:00Z");

        Assert.Equal("2026-08-06 09:05", MomentText.In(morning, TimeZoneInfo.Utc));
        Assert.Equal("2026-08-06 21:47", MomentText.In(evening, TimeZoneInfo.Utc));
        Assert.NotEqual(
            MomentText.In(morning, TimeZoneInfo.Utc),
            MomentText.In(evening, TimeZoneInfo.Utc));
    }

    [Fact]
    public void TwoFactsMinutesApart_AreDistinguishable()
    {
        var first = At("2026-08-06T14:31:00Z");
        var second = At("2026-08-06T14:33:00Z");

        Assert.NotEqual(MomentText.In(first, Pacific), MomentText.In(second, Pacific));
    }

    [Fact]
    public void InUtc_TheRenderMatchesTheStoredInstant()
    {
        Assert.Equal("2026-08-07 01:30", MomentText.In(At("2026-08-07T01:30:00Z"), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Local_UsesTheMachineZone()
    {
        var instant = At("2026-08-07T01:30:00Z");

        Assert.Equal(MomentText.In(instant, TimeZoneInfo.Local), MomentText.Local(instant));
    }
}
