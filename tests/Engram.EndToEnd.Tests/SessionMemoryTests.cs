using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Engram.EndToEnd.Tests;

public class SessionMemoryTests
{
    [Fact]
    public async Task Remember_PreCompact_Recall_ReturnsSessionHandleAndSurvivesCompactionAccordingToProbe()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var cancellationToken = TestContext.Current.CancellationToken;

        var transport = new StdioClientTransport(new()
        {
            Name = "engram-e2e-session-memory-test",
            Command = EndToEndBinary.Path!,
            Arguments = ["mcp"],
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?> { ["ENGRAM_HOME"] = home.Root },
        });

        string recallText;
        await using (var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken))
        {
            await client.CallToolAsync(
                "engram_remember",
                new Dictionary<string, object?> { ["statement"] = "The nightly ETL job dedups on customer_id before load." },
                cancellationToken: cancellationToken);

            var (hookExitCode, _, hookStderr) = EngramProcess.RunWithStdin(home.Root, """{"session_id":"e2e-precompact"}""", "hook", "pre-compact");
            Assert.Equal(0, hookExitCode);
            Assert.Equal(string.Empty, hookStderr);

            var recall = await client.CallToolAsync(
                "engram_recall",
                new Dictionary<string, object?> { ["query"] = "nightly ETL dedups customer_id" },
                cancellationToken: cancellationToken);
            recallText = ExtractText(recall);
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
    public async Task Remember_InFirstMcpProcess_IsRecalledAsPriorSessionFactInSecondMcpProcess()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var cancellationToken = TestContext.Current.CancellationToken;

        var writerTransport = new StdioClientTransport(new()
        {
            Name = "engram-e2e-session-memory-writer",
            Command = EndToEndBinary.Path!,
            Arguments = ["mcp"],
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?> { ["ENGRAM_HOME"] = home.Root },
        });

        await using (var writer = await McpClient.CreateAsync(writerTransport, cancellationToken: cancellationToken))
        {
            await writer.CallToolAsync(
                "engram_remember",
                new Dictionary<string, object?> { ["statement"] = "The staging database migration ran clean on 2026-08-04." },
                cancellationToken: cancellationToken);
        }

        var (hookExitCode, _, hookStderr) = EngramProcess.RunWithStdin(home.Root, """{"session_id":"e2e-precompact-prior"}""", "hook", "pre-compact");
        Assert.Equal(0, hookExitCode);
        Assert.Equal(string.Empty, hookStderr);

        var readerTransport = new StdioClientTransport(new()
        {
            Name = "engram-e2e-session-memory-reader",
            Command = EndToEndBinary.Path!,
            Arguments = ["mcp"],
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?> { ["ENGRAM_HOME"] = home.Root },
        });

        string recallText;
        await using (var reader = await McpClient.CreateAsync(readerTransport, cancellationToken: cancellationToken))
        {
            var recall = await reader.CallToolAsync(
                "engram_recall",
                new Dictionary<string, object?> { ["query"] = "staging database migration" },
                cancellationToken: cancellationToken);
            recallText = ExtractText(recall);
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

    private static string ExtractText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
