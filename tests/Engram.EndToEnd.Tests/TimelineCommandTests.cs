using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Tier 3 (D9): <c>engram timeline</c> against the published binary
/// (docs/memory-expansion/05-browse-tui-spec.md).
/// </summary>
public partial class TimelineCommandTests
{
    [GeneratedRegex(@"\[f(\d+)\]")]
    private static partial Regex HandlePattern();

    private static string ExtractHandle(string mcpResponseText)
    {
        var match = HandlePattern().Match(mcpResponseText);
        Assert.True(match.Success, $"no fact handle found in: {mcpResponseText}");
        return "f" + match.Groups[1].Value;
    }

    [Fact]
    public async Task Timeline_PrintsNeighboursAndEmitsTelemetry()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        string firstHandle;
        string anchorHandle;
        string lastHandle;
        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);

            firstHandle = ExtractHandle(await client.CallToolTextAsync(
                "engram_remember", new JsonObject { ["statement"] = "The timeline e2e test wrote the first fact." }, cancellationToken));
            anchorHandle = ExtractHandle(await client.CallToolTextAsync(
                "engram_remember", new JsonObject { ["statement"] = "The timeline e2e test wrote the anchor fact." }, cancellationToken));
            lastHandle = ExtractHandle(await client.CallToolTextAsync(
                "engram_remember", new JsonObject { ["statement"] = "The timeline e2e test wrote the last fact." }, cancellationToken));
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "timeline", anchorHandle, "--before", "1", "--after", "1");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains($"[{firstHandle}]", stdout, StringComparison.Ordinal);
        Assert.Contains($"[{anchorHandle}]", stdout, StringComparison.Ordinal);
        Assert.Contains($"[{lastHandle}]", stdout, StringComparison.Ordinal);

        var telemetryPath = Path.Combine(home.Root, "telemetry.jsonl");
        var telemetryRecords = File.ReadAllLines(telemetryPath).Select(line => JsonDocument.Parse(line).RootElement);
        Assert.Contains(telemetryRecords, r => r.GetProperty("kind").GetString() == "timeline");
    }

    [Fact]
    public void Timeline_UnknownHandle_ReportsNoFact_WithoutCrashing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "timeline", "f999999");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("No fact 'f999999'", stdout, StringComparison.Ordinal);
    }
}
