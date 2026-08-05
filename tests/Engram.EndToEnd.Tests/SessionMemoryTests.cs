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

        // The whole line, because the tier is carried by the annotation: a note that came back
        // ranked as prior-session memory would still contain the body and the handle.
        Assert.Matches(@"\[f\d+\] The nightly ETL job dedups on customer_id before load\. \(session\)", recallText);

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

        Assert.Matches(
            @"\[f\d+\] The staging database migration ran clean on 2026-08-04\. \(session · p1 · \d+d\)",
            recallText);

        // "(session)" with nothing after it is the current-session annotation, and this note
        // was taken in a different one.
        Assert.DoesNotContain("(session)", recallText);

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "probe", "--json");
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var report = JsonDocument.Parse(stdout);
        var survival = report.RootElement.GetProperty("compaction_survival");
        Assert.Equal(0, survival.GetProperty("events").GetInt32());

        var priorRecallStat = report.RootElement.GetProperty("sessions_with_prior_session_fact_recall");
        Assert.Equal(1, priorRecallStat.GetProperty("count").GetInt32());
    }

    /// <summary>
    /// Tier 3, because <c>engram_digest</c> persisting is a claim about what ships. It stored
    /// nothing at all until now, so a JIT-only test would be proving the new behaviour against
    /// the build that is not installed.
    /// </summary>
    [Fact]
    public async Task Digest_PersistsItsLearnings_AndRepeatingOneReturnsTheSameHandle()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var port = FreeTcpPort.Next();
        var cancellationToken = TestContext.Current.CancellationToken;

        var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
        Assert.True(startExit == 0, $"engram start failed: {startErr}");

        string firstDigest;
        string secondDigest;
        string recallText;
        try
        {
            using var client = new HttpMcpClient(port);
            await client.InitializeAsync(cancellationToken);

            firstDigest = await client.CallToolTextAsync(
                "engram_digest",
                new JsonObject
                {
                    ["learnings"] = new JsonArray(
                        "The report exporter writes UTF-8 with a BOM because Excel needs one.",
                        "Rotating the signing key requires draining the queue first."),
                    ["session_summary"] = "Chased an encoding bug into the exporter and fixed it.",
                },
                cancellationToken);

            secondDigest = await client.CallToolTextAsync(
                "engram_digest",
                new JsonObject
                {
                    ["learnings"] = new JsonArray(
                        "The report exporter writes UTF-8 with a BOM because Excel needs one."),
                },
                cancellationToken);

            recallText = await client.CallToolTextAsync(
                "engram_recall",
                new JsonObject { ["query"] = "signing key rotation queue" },
                cancellationToken);
        }
        finally
        {
            EngramProcess.Run(home.Root, "stop");
        }

        Assert.Matches(@"^2 learning\(s\) stored as session notes \[f\d+ f\d+\]\.", firstDigest);
        Assert.Contains("Session summary recorded", firstDigest, StringComparison.Ordinal);

        // The same statement digested twice keeps the handle it was first given, so a model
        // that digests at compaction and again at the end does not double the store.
        var firstHandle = FirstHandle(firstDigest);
        Assert.Equal(firstHandle, FirstHandle(secondDigest));

        // Body words absent from the query: recall echoes the query in its header and its gap
        // message, so asserting on a query term passes even against an empty store.
        Assert.Contains("draining the queue first", recallText, StringComparison.Ordinal);
    }

    private static string FirstHandle(string digestResponse)
    {
        var open = digestResponse.IndexOf('[', StringComparison.Ordinal);
        var close = digestResponse.IndexOf(']', StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, $"expected bracketed handles, got: {digestResponse}");

        return digestResponse[(open + 1)..close].Split(' ')[0];
    }
}
