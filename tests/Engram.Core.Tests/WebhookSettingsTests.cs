using System.Reflection;
using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// Tier 1. The section is small; what it has to get right is which mistakes switch delivery off
/// and which only narrow it.
/// </summary>
public class WebhookSettingsTests
{
    private static WebhookSettings Read(string toml) =>
        WebhookSettings.Read(ConfigFile.Parse(toml));

    [Fact]
    public void NoSection_DeliversNothingAndComplainsAboutNothing()
    {
        var settings = Read("[embedding]\nprovider = \"none\"\n");

        Assert.False(settings.IsEnabled);
        Assert.Empty(settings.Problems);
        Assert.Empty(settings.Urls);
    }

    [Fact]
    public void AConfiguredUrl_IsTheSwitch()
    {
        var settings = Read("[webhook]\nurl = \"http://127.0.0.1:8787/engram\"\n");

        Assert.True(settings.IsEnabled);
        Assert.Equal(["http://127.0.0.1:8787/engram"], settings.Urls);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("/just/a/path")]
    public void AUrlThatIsNotHttp_IsAProblemAndDeliversNothing(string url)
    {
        var settings = Read($"[webhook]\nurl = \"{url}\"\n");

        Assert.False(settings.IsEnabled);
        Assert.Single(settings.Problems);
        Assert.Contains(url, settings.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void UrlAndUrls_AreUnionedAndDeduplicated()
    {
        var settings = Read(
            """
            [webhook]
            url = "http://127.0.0.1:8787/engram"
            urls = ["http://127.0.0.1:8787/engram", "http://127.0.0.1:9000/hook"]
            """);

        Assert.Equal(
            ["http://127.0.0.1:8787/engram", "http://127.0.0.1:9000/hook"],
            settings.Urls);
    }

    [Fact]
    public void WithNoKindsNamed_EveryEventIsWanted()
    {
        var settings = Read("[webhook]\nurl = \"http://127.0.0.1:8787/engram\"\n");

        Assert.True(settings.Wants(TelemetryEventKind.Remember));
        Assert.True(settings.Wants(TelemetryEventKind.FileTouched));
    }

    [Fact]
    public void NamedKinds_NarrowTheStream()
    {
        var settings = Read(
            """
            [webhook]
            url = "http://127.0.0.1:8787/engram"
            kinds = ["remember", "recall"]
            """);

        Assert.True(settings.Wants(TelemetryEventKind.Remember));
        Assert.True(settings.Wants(TelemetryEventKind.Recall));
        Assert.False(settings.Wants(TelemetryEventKind.FileTouched));
    }

    /// <summary>
    /// The load-bearing one. A misfiled kind must cost that kind and nothing else — folding it
    /// into <see cref="WebhookSettings.Problems"/> would clear <see cref="WebhookSettings.IsEnabled"/>
    /// and stop delivering the kinds that were spelled correctly, which is the same trap a retired
    /// embedding key set for the vector lane.
    /// </summary>
    [Fact]
    public void AnUnknownKind_IsReportedWithoutSwitchingDeliveryOff()
    {
        var settings = Read(
            """
            [webhook]
            url = "http://127.0.0.1:8787/engram"
            kinds = ["remember", "rememberr"]
            """);

        Assert.True(settings.IsEnabled);
        Assert.Empty(settings.Problems);
        Assert.Single(settings.Unknown);
        Assert.Contains("rememberr", settings.Unknown[0], StringComparison.Ordinal);
        Assert.True(settings.Wants(TelemetryEventKind.Remember));
    }

    [Fact]
    public void TheWildcard_IsNotAnUnknownKind()
    {
        var settings = Read("[webhook]\nurl = \"http://127.0.0.1:8787/engram\"\nkinds = [\"*\"]\n");

        Assert.Empty(settings.Unknown);
    }

    /// <summary>
    /// Every constant on <see cref="TelemetryEventKind"/> has to be in its own <c>All</c>, or a
    /// real filter gets reported to the user as a typo and delivers nothing. Nothing else notices.
    /// </summary>
    /// <remarks>
    /// Read off the constants by reflection rather than by walking <c>All</c>. Walking <c>All</c>
    /// to check <c>All</c> is a tautology — a kind dropped from the list is simply not visited, so
    /// the test passes with the defect in place. Measured: it did.
    /// </remarks>
    [Fact]
    public void EveryKindConstant_IsListedInAll()
    {
        var declared = typeof(TelemetryEventKind)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false }
                            && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.Equal(declared.Order(), TelemetryEventKind.All.Order());
    }

    [Fact]
    public void EveryEmittedKind_IsAcceptedAsAFilter()
    {
        foreach (var kind in TelemetryEventKind.All)
        {
            var settings = Read($"[webhook]\nurl = \"http://127.0.0.1:1/x\"\nkinds = [\"{kind}\"]\n");

            Assert.Empty(settings.Unknown);
            Assert.True(settings.Wants(kind));
        }
    }

    [Fact]
    public void ANonPositiveTimeout_IsAProblemAndFallsBackToTheDefault()
    {
        var settings = Read("[webhook]\nurl = \"http://127.0.0.1:8787/engram\"\ntimeout_ms = 0\n");

        Assert.Single(settings.Problems);
        Assert.Equal(
            TimeSpan.FromMilliseconds(WebhookSettings.DefaultTimeoutMilliseconds),
            settings.Timeout);
    }

    [Fact]
    public void TheShippedDefault_ConfiguresNoSubscriber()
    {
        var settings = WebhookSettings.Read(ConfigFile.Parse(DefaultConfig.Content));

        Assert.False(settings.IsEnabled);
        Assert.Empty(settings.Problems);
        Assert.Empty(settings.Unknown);
    }
}
