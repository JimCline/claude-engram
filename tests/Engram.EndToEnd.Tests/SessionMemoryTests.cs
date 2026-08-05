using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

public class SessionMemoryTests
{
    [Fact]
    public async Task Remember_PreCompact_Recall_ReturnsSessionHandleAndSurvivesCompactionAccordingToProbe()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        string recallText;
        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);

            await client.CallToolTextAsync(
                "engram_remember",
                new JsonObject { ["statement"] = "The nightly ETL job dedups on customer_id before load." },
                cancellationToken);

            var (hookExitCode, _, hookStderr) = EngramProcess.RunWithStdin(home.Root, """{"session_id":"e2e-precompact"}""", "hook", "pre-compact");
            Assert.Equal(0, hookExitCode);
            Assert.Equal(string.Empty, hookStderr);

            recallText = await client.CallToolTextAsync(
                "engram_recall",
                new JsonObject { ["query"] = "nightly ETL dedups customer_id" },
                cancellationToken);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        Assert.Contains("[s001]", recallText);
        Assert.Contains("nightly ETL job dedups", recallText);

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "probe", "--json");
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var report = JsonDocument.Parse(stdout);
        var survival = report.RootElement.GetProperty("compaction_survival");
        Assert.True(survival.GetProperty("events").GetInt32() >= 1);
    }

    [Fact]
    public async Task Remember_InFirstMcpSession_IsRecalledAsPriorSessionFactInSecondMcpSession()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        string recallText;
        try
        {
            using (var writer = new HttpMcpClient(port))
            {
                await writer.InitializeAsync(cancellationToken);
                await writer.CallToolTextAsync(
                    "engram_remember",
                    new JsonObject { ["statement"] = "The staging database migration ran clean on 2026-08-04." },
                    cancellationToken);
            }

            var (hookExitCode, _, hookStderr) = EngramProcess.RunWithStdin(home.Root, """{"session_id":"e2e-precompact-prior"}""", "hook", "pre-compact");
            Assert.Equal(0, hookExitCode);
            Assert.Equal(string.Empty, hookStderr);

            using var reader = new HttpMcpClient(port);
            await reader.InitializeAsync(cancellationToken);
            recallText = await reader.CallToolTextAsync(
                "engram_recall",
                new JsonObject { ["query"] = "staging database migration" },
                cancellationToken);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        Assert.Contains("staging database migration ran clean", recallText);
        Assert.Contains("@p1]", recallText);
        Assert.Contains("(session · ", recallText);
        Assert.Contains("d)", recallText);
        Assert.DoesNotContain("[s001] ", recallText);

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "probe", "--json");
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var report = JsonDocument.Parse(stdout);
        var survival = report.RootElement.GetProperty("compaction_survival");
        Assert.Equal(0, survival.GetProperty("events").GetInt32());

        var priorRecallStat = report.RootElement.GetProperty("sessions_with_prior_session_fact_recall");
        Assert.Equal(1, priorRecallStat.GetProperty("count").GetInt32());
    }
}
