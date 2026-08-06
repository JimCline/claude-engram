using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 (D9). <c>engram_digest</c> used to confirm receipt and drop everything, which was
/// deliberate while there was no store to write to. These pin the behaviour now that there is.
/// </summary>
public class DigestTests
{
    private static readonly McpHomeState Initialized = new(true);

    /// <summary>Never asked to start anything — these homes configure no embedding provider.</summary>
    private static LocalRuntime NoRuntime(EngramHome home) => new(home, _ => null);

    [Fact]
    public void Digest_StoresEachLearningAsASessionNote_AndRecallFindsThem()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("digest-a");

        var response = EngramMcpTools.Digest(
            sandbox.Home,
            session,
            Initialized,
            [
                "The report generator reads its column order from etc/columns.toml, not from the database.",
                "Retrying the webhook more than twice trips the provider's rate limiter.",
            ]);

        var handles = HandlesOf(response);
        Assert.Equal(2, handles.Count);

        var recall = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "webhook retry rate limiter");

        // Body words absent from the query: recall echoes the query in its header and its gap
        // message, so asserting on a query term passes even against an empty store.
        Assert.Contains("trips the provider's rate limiter", recall, StringComparison.Ordinal);
        Assert.Contains($"[{handles[1]}]", recall, StringComparison.Ordinal);
    }

    /// <summary>
    /// The description promises calling digest at compaction and again at the end is safe, so
    /// a repeat has to be free rather than merely tolerated.
    /// </summary>
    [Fact]
    public void Digest_RepeatedLearning_ReturnsTheSameHandleAndAddsNoSecondFact()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("digest-repeat");
        const string Learning = "The staging cluster runs one node fewer than production on purpose.";

        var first = HandlesOf(EngramMcpTools.Digest(sandbox.Home, session, Initialized, [Learning]));
        var before = LiveFactCount(sandbox);

        var second = HandlesOf(EngramMcpTools.Digest(sandbox.Home, session, Initialized, [Learning]));

        Assert.Equal(first, second);
        Assert.Equal(before, LiveFactCount(sandbox));
    }

    [Fact]
    public void Digest_SessionSummary_LandsInTheSessionRow()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("digest-summary");

        EngramMcpTools.Digest(
            sandbox.Home, session, Initialized, ["Nothing surprising in the parser."],
            session_summary: "Traced the encoding bug to the CSV reader and fixed it.");

        Assert.Equal("Traced the encoding bug to the CSV reader and fixed it.", DigestOf(sandbox, session.Value));
    }

    /// <summary>
    /// A second digest has seen strictly more of the session, so its summary wins. The
    /// learnings are append-only; this one field is not, and that asymmetry is the point.
    /// </summary>
    [Fact]
    public void Digest_SecondSummary_ReplacesTheFirstWhileLearningsAccumulate()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("digest-twice");

        EngramMcpTools.Digest(
            sandbox.Home, session, Initialized, ["The importer skips rows with a null tenant."],
            session_summary: "Halfway through the import audit.");
        EngramMcpTools.Digest(
            sandbox.Home, session, Initialized, ["The exporter does not skip them, which is the bug."],
            session_summary: "Import audit complete: the asymmetry between importer and exporter was the bug.");

        Assert.Equal(
            "Import audit complete: the asymmetry between importer and exporter was the bug.",
            DigestOf(sandbox, session.Value));

        var recall = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "importer exporter null tenant");
        Assert.Contains("skips rows with a null tenant", recall, StringComparison.Ordinal);
        Assert.Contains("which is the bug", recall, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_SummaryWithNoLearnings_StillRecordsTheSummary()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("digest-summary-only");

        var response = EngramMcpTools.Digest(
            sandbox.Home, session, Initialized, [], session_summary: "Read a lot, concluded nothing worth keeping.");

        Assert.Equal("Read a lot, concluded nothing worth keeping.", DigestOf(sandbox, session.Value));
        Assert.Contains("Session summary recorded", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// Overflow is reported rather than silently dropped: a model that believes 30 learnings
    /// landed will not resend the 5 that did not.
    /// </summary>
    [Fact]
    public void Digest_BeyondTheCap_StoresTheCapAndSaysWhatItDropped()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("digest-overflow");
        var learnings = Enumerable
            .Range(0, EngramMcpTools.MaxDigestLearnings + 5)
            .Select(i => $"Queue worker {i} drains its own partition and never another's.")
            .ToArray();

        var before = LiveFactCount(sandbox);
        var response = EngramMcpTools.Digest(sandbox.Home, session, Initialized, learnings);

        Assert.Equal(EngramMcpTools.MaxDigestLearnings, HandlesOf(response).Count);
        Assert.Equal(before + EngramMcpTools.MaxDigestLearnings, LiveFactCount(sandbox));
        Assert.Contains("5 learning(s) beyond the 25-cap were not stored", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_BlankEntries_AreSkippedRatherThanStored()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("digest-blank");

        var before = LiveFactCount(sandbox);
        var response = EngramMcpTools.Digest(
            sandbox.Home, session, Initialized, ["", "   ", "The cache key includes the locale."]);

        Assert.Single(HandlesOf(response));
        Assert.Equal(before + 1, LiveFactCount(sandbox));
        Assert.Contains("2 blank", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_OnAnUninitialisedHome_SavesNothingAndSaysSo()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var session = new McpSessionId("digest-uninitialised");

        var response = EngramMcpTools.Digest(
            sandbox.Home, session, new McpHomeState(false), ["This should not be stored anywhere."]);

        Assert.Contains("not initialised", response, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.Home.DatabasePath));
    }

    /// <summary>
    /// The description tells the model it can pass a returned id to <c>engram_forget</c>. If
    /// that were untrue, the only way to find out would be a user trying to retract something.
    /// </summary>
    [Fact]
    public void Digest_ThenForgetOneOfItsHandles_RetractsThatNote()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("digest-forget");

        var handles = HandlesOf(EngramMcpTools.Digest(
            sandbox.Home,
            session,
            Initialized,
            [
                "The audit log keeps deletions for ninety days.",
                "The nightly vacuum runs before the backup, not after.",
            ]));

        EngramMcpTools.Forget(sandbox.Home, session, Initialized, handles[0]);

        var recall = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "audit log vacuum backup");

        Assert.DoesNotContain($"[{handles[0]}]", recall, StringComparison.Ordinal);
        Assert.DoesNotContain("ninety days", recall, StringComparison.Ordinal);
        Assert.Contains($"[{handles[1]}]", recall, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> HandlesOf(string response)
    {
        var open = response.IndexOf('[', StringComparison.Ordinal);
        var close = response.IndexOf(']', StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, $"expected bracketed handles, got: {response}");

        return response[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? DigestOf(SandboxHome sandbox, string externalId)
    {
        using var connection = EngramDatabase.Open(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT digest FROM session WHERE external_id = $external;";
        command.Parameters.AddWithValue("$external", externalId);

        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    // Counted rather than matched on body text, so this does not depend on how a note is
    // spelled into the fact row.
    private static long LiveFactCount(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.Open(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fact WHERE valid_to IS NULL;";

        return Convert.ToInt64(command.ExecuteScalar()!);
    }
}
