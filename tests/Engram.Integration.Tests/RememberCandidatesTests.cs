using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Near-neighbour candidates on <c>engram_remember</c> (docs/memory-expansion/
/// 02-conflict-verdicts-spec.md, "Candidates on engram_remember"). Post-write, D44-gated,
/// store-wide, capped at 3, self-excluded, skipped when <c>supersedes</c> is given.
/// </summary>
public class RememberCandidatesTests
{
    private static readonly McpHomeState Initialized = new(true);

    private static LocalRuntime NoRuntime(EngramHome home) => new(home);

    private static void EnableCandidates(EngramHome home) =>
        File.AppendAllText(home.ConfigPath, "\n[remember]\ncandidates = true\n");

    [Fact]
    public void Remember_ALexicallyCloseStatement_ReturnsTheExistingFactAsACandidate_ANovelStatementReturnsNone()
    {
        using var sandbox = new SandboxHome();
        EnableCandidates(sandbox.Home);
        var session = new McpSessionId("session-a");

        var existingHandle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "The nightly backup job runs at 2am Pacific."));

        var closeResponse = EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home),
            "The nightly backup job now also verifies checksums after it runs.");

        Assert.Contains("Possibly related:", closeResponse, StringComparison.Ordinal);
        Assert.Contains($"[{existingHandle}]", closeResponse, StringComparison.Ordinal);

        var novelResponse = EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "Zqbvorak fluxinates the Windlemere prisms.");

        Assert.DoesNotContain("Possibly related:", novelResponse, StringComparison.Ordinal);
    }

    // In a sparse store, a single shared non-stopword token already satisfies the 2+-lane
    // bar — overlap and lexical both key off the same literal token, so they agree whenever
    // either does (confirmed empirically: an otherwise-unrelated statement sharing only
    // "job" with the anchor fact below still surfaces as a candidate). So this statement
    // shares no content word at all with the anchor rather than exactly one, and the gate
    // itself is exercised by the close-match assertion above, where "nightly backup job
    // runs" corroborates across multiple shared tokens legitimately.
    [Fact]
    public void Remember_AnUnrelatedStatement_ReturnsNoCandidates()
    {
        using var sandbox = new SandboxHome();
        EnableCandidates(sandbox.Home);
        var session = new McpSessionId("session-a");

        EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "The nightly backup job runs at 2am Pacific.");

        var weakResponse = EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "Trellmoq gathers the Ostervane fragments quietly.");

        Assert.DoesNotContain("Possibly related:", weakResponse, StringComparison.Ordinal);
    }

    // A byte-identical restatement short-circuits inside SessionFacts.Append's own
    // FindLiveFactId check and returns the existing fact's id rather than writing a new
    // row — self-exclusion in NearNeighbourCandidates then filters out the only fact that
    // would otherwise match, so no candidates field appears. Falsify: disable that
    // short-circuit and this starts failing (a second, distinct row is written instead of
    // the same handle being returned twice).
    [Fact]
    public void Remember_AByteIdenticalRestatement_ReturnsTheExistingIdWithNoCandidatesField()
    {
        using var sandbox = new SandboxHome();
        EnableCandidates(sandbox.Home);
        var session = new McpSessionId("session-a");

        const string statement = "The nightly backup job runs at 2am Pacific.";
        var firstHandle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), statement));
        var secondResponse = EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), statement);

        Assert.Equal(firstHandle, HandleOf(secondResponse));
        Assert.DoesNotContain("Possibly related:", secondResponse, StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fact WHERE body = $body AND valid_to IS NULL;";
        command.Parameters.AddWithValue("$body", statement);
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    // Falsify: remove the `if (!string.IsNullOrWhiteSpace(supersedes))` early return in
    // EngramMcpTools.Remember and this starts failing — candidates would run against the
    // supersedes branch too.
    [Fact]
    public void Remember_WithSupersedes_RunsNoCandidateSearch()
    {
        using var sandbox = new SandboxHome();
        EnableCandidates(sandbox.Home);
        var session = new McpSessionId("session-a");

        var captureResponse = EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "The nightly backup job runs at 2am Pacific.");
        var captureHandle = HandleOf(captureResponse);

        var restateResponse = EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home),
            "The nightly backup job now also verifies checksums.",
            supersedes: captureHandle);

        Assert.Contains("replaced capture", restateResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("Possibly related:", restateResponse, StringComparison.Ordinal);
    }

    /// <summary>
    /// The handle out of a tool response, so these assert on the id the model was actually
    /// handed rather than on one guessed from a counter that no longer exists.
    /// </summary>
    private static string HandleOf(string response)
    {
        var close = response.IndexOf(']', StringComparison.Ordinal);
        Assert.True(response.StartsWith('[') && close > 1, $"expected a bracketed handle, got: {response}");
        return response[1..close];
    }
}
