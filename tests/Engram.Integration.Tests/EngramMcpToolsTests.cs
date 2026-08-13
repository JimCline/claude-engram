using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class EngramMcpToolsTests
{
    private static readonly McpHomeState Initialized = new(true);

    /// <summary>
    /// A runtime that will never be asked to start anything: these homes configure no provider,
    /// so the vector lane refuses before a model is ever considered. Constructing one launches
    /// nothing, which is the property that makes it safe to hand over and drop.
    /// </summary>
    private static LocalRuntime NoRuntime(EngramHome home) => new(home);

    // Recall's long-term tier now comes from SQLite rather than a hardcoded list. Every other
    // test here exercises session facts, so without this one the whole store could return
    // nothing and the suite would still pass.
    [Fact]
    public void Recall_ReturnsLongTermFactsReadFromTheStore()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-longterm");

        var result = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "BEGIN IMMEDIATE transaction");

        // Assert on words from the fact's BODY that do not appear in the query. Recall echoes
        // the query in its header line and again in its gap message, so asserting on a query
        // term passes even when the store returns nothing at all — checked, by emptying it.
        Assert.Contains("SQLITE_BUSY_SNAPSHOT", result, StringComparison.Ordinal);
        Assert.DoesNotContain("0 facts", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Remember_ThenRecallSameSession_ReturnsTheNoteInTheSessionTier()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "The build pipeline retries flaky uploads three times before failing."));

        var result = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "flaky uploads retries");

        Assert.Contains($"[{handle}]", result);
        Assert.Contains("three times before failing", result);
        Assert.Contains("(session)", result);
    }

    [Fact]
    public void Remember_ThenRecallDifferentSession_ReturnsThePriorSessionNoteWithItsSessionMarked()
    {
        using var sandbox = new SandboxHome();
        var writer = new McpSessionId("session-a");
        var reader = new McpSessionId("session-b");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, writer, Initialized, "The build pipeline retries flaky uploads three times before failing."));

        var result = EngramMcpTools.Recall(sandbox.Home, reader, Initialized, NoRuntime(sandbox.Home), "flaky uploads retries");

        Assert.Contains($"[{handle}]", result);
        Assert.Contains("session · p1 ·", result);
        Assert.Contains("three times before failing", result);
        Assert.DoesNotContain("coverage: none", result);
    }

    [Fact]
    public void Recall_CurrentSessionNoteRanksAboveAPriorSessionNote_AndBothAreDistinguishable()
    {
        using var sandbox = new SandboxHome();
        var priorSession = new McpSessionId("session-old");
        var currentSession = new McpSessionId("session-new");

        var priorHandle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, priorSession, Initialized, "The nightly backup job runs at 2am UTC."));
        var currentHandle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, currentSession, Initialized, "The nightly backup job now also verifies checksums."));

        var result = EngramMcpTools.Recall(sandbox.Home, currentSession, Initialized, NoRuntime(sandbox.Home), "nightly backup job");

        var currentHandleIndex = result.IndexOf($"[{currentHandle}]", StringComparison.Ordinal);
        var priorHandleIndex = result.IndexOf($"[{priorHandle}]", StringComparison.Ordinal);

        Assert.True(currentHandleIndex >= 0, $"current-session handle [{currentHandle}] should be present");
        Assert.True(priorHandleIndex >= 0, $"prior-session handle [{priorHandle}] should be present");
        Assert.True(currentHandleIndex < priorHandleIndex, "current-session fact must rank above the prior-session fact");
    }

    // The reason session notes moved onto the store. In the JSONL format there was no way to
    // express a retracted note, so engram_forget refused them outright and a mistaken note
    // stayed recallable for good.
    [Fact]
    public void Forget_RetractsASessionNoteAndItStopsBeingRecalled()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "The build pipeline retries flaky uploads three times before failing."));

        var response = EngramMcpTools.Forget(sandbox.Home, session, Initialized, handle);
        Assert.Contains("Retracted", response);

        // On the body, not the query: recall echoes the query in its header and again in the
        // gap message, so asserting the query terms are gone passes even when nothing was
        // retracted at all.
        var result = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "flaky uploads retries");
        Assert.DoesNotContain("three times before failing", result);
        Assert.DoesNotContain($"[{handle}]", result);
    }

    [Fact]
    public void Remember_ReturnsAFactHandleInResponseText()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var response = EngramMcpTools.Remember(sandbox.Home, session, Initialized, "Statement one.");

        Assert.Matches(@"^\[f\d+\] remembered:", response);
    }

    [Fact]
    public void Remember_WithAgentName_AttributesTheNoteToThatAgent()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        EngramMcpTools.Remember(sandbox.Home, session, Initialized, "Ran the migration dry-run against staging.", agent: "migration-worker");

        var result = EngramMcpTools.Recall(sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "migration dry-run staging");

        Assert.Contains("session · migration-worker", result);
    }

    [Fact]
    public void Remember_UninitialisedHome_DoesNotPersist()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var session = new McpSessionId("session-a");

        var response = EngramMcpTools.Remember(sandbox.Home, session, new McpHomeState(false), "Statement one.");

        Assert.DoesNotContain("remembered:", response);
        Assert.False(File.Exists(sandbox.Home.DatabasePath));
    }

    [Fact]
    public void Remember_DetailsOverTheCeiling_RefusesAndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");
        var details = new string('x', 8000);

        var response = EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "Statement about a rare zebrafish migration pattern.", details: details);

        Assert.Contains("2,000-token ceiling", response);
        Assert.Contains("Nothing was stored", response);
        Assert.DoesNotMatch(@"^\[f\d+\]", response);

        // The query text itself is echoed in Recall's header regardless of what matched, so the
        // check is on a longer phrase than the query — one that can only appear if the fact's
        // own body was actually returned.
        var result = EngramMcpTools.Recall(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "zebrafish migration");
        Assert.DoesNotContain("rare zebrafish migration pattern", result);
    }

    // The ceiling applies to details only — a statement is never rejected for length, even one
    // long enough to have tripped the details ceiling had it been passed as details.
    [Fact]
    public void Remember_VeryLongStatement_IsNeverRejected()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");
        var longStatement = new string('w', 8000);

        var response = EngramMcpTools.Remember(sandbox.Home, session, Initialized, longStatement);

        Assert.Matches(@"^\[f\d+\] remembered:", response);
    }

    [Fact]
    public void Remember_WithDetails_TheDetailsViewReturnsBodyAndDetails()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "Short statement.", details: "The depth that didn't fit in the statement."));

        var result = EngramMcpTools.Expand(sandbox.Home, session, Initialized, handle, "details");

        Assert.Equal("Short statement.\n\nThe depth that didn't fit in the statement.", result);
    }

    [Fact]
    public void Revise_DetailsOverTheCeiling_RefusesAndLeavesTheFactUnrevised()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");
        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "The nightly backup job runs at 2am UTC."));
        var details = new string('x', 8000);

        var response = EngramMcpTools.Revise(
            sandbox.Home, session, Initialized, handle, "The nightly backup job runs at 3am UTC.", "corrected the time",
            details: details);

        Assert.Contains("2,000-token ceiling", response);
        Assert.Contains("Nothing was stored", response);

        var result = EngramMcpTools.Recall(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "nightly backup job runs");
        Assert.Contains("2am UTC", result);
        Assert.DoesNotContain("3am UTC", result);
    }

    // Pinned by a falsification: making Revise default details to the target's own Details
    // (simulating carry-forward) turns this red; restoring `Details: details` turns it green.
    [Fact]
    public void Revise_WithoutDetails_DoesNotCarryDetailsForwardFromTheOldVersion()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var original = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "The deploy script targets us-east-1.",
            details: "Config: region=us-east-1, replicas=3."));

        var revised = HandleOf(EngramMcpTools.Revise(
            sandbox.Home, session, Initialized, original, "The deploy script targets us-west-2.", "region moved"));

        var result = EngramMcpTools.Expand(sandbox.Home, session, Initialized, revised, "details");

        Assert.Equal("The deploy script targets us-west-2.", result);
        Assert.DoesNotContain("us-east-1", result);
    }

    [Fact]
    public void Expand_DetailsView_WithNoDetailsStored_ReturnsJustTheBody()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "A fact with no depth beyond its statement."));

        var result = EngramMcpTools.Expand(sandbox.Home, session, Initialized, handle, "details");

        Assert.Equal("A fact with no depth beyond its statement.", result);
        Assert.DoesNotContain("continue with offset", result);
    }

    [Fact]
    public void Expand_DetailsView_OffsetPastTheEnd_ReturnsAnError()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(sandbox.Home, session, Initialized, "Short."));

        var result = EngramMcpTools.Expand(sandbox.Home, session, Initialized, handle, "details", offset: 9999);

        Assert.Matches(@"^offset 9999 is past the end \(\d+ chars\)\.$", result);
    }

    // The window lands mid-surrogate-pair by construction: 356 filler chars plus a 100-token
    // budget (360 chars) puts the cut exactly on the emoji's low surrogate, which is the one
    // case the step-back-one-char rule exists for.
    [Fact]
    public void Expand_DetailsView_NeverSplitsASurrogatePairAcrossAPageBoundary()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        const string emoji = "😀";
        var details = new string('a', 356) + emoji + new string('b', 50);
        var handle = HandleOf(EngramMcpTools.Remember(sandbox.Home, session, Initialized, "s", details: details));
        var fullText = "s\n\n" + details;

        var page1 = EngramMcpTools.Expand(sandbox.Home, session, Initialized, handle, "details", budget_tokens: 100);

        Assert.DoesNotContain(emoji, page1);
        var match = System.Text.RegularExpressions.Regex.Match(page1, @"continue with offset: (\d+)");
        Assert.True(match.Success, $"expected a continuation line, got: {page1}");
        var nextOffset = int.Parse(match.Groups[1].Value);

        var page2 = EngramMcpTools.Expand(
            sandbox.Home, session, Initialized, handle, "details", budget_tokens: 100, offset: nextOffset);

        Assert.Contains(emoji, page2);
        Assert.DoesNotContain("continue with offset", page2);

        var page1Body = page1[..page1.IndexOf("\n\nshowing chars", StringComparison.Ordinal)];
        Assert.Equal(fullText, page1Body + page2);
    }

    [Fact]
    public void Expand_DetailsView_BudgetTokensZero_ReturnsTheExactErrorAndNothingElse()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "Short.", details: "Some depth."));

        var result = EngramMcpTools.Expand(sandbox.Home, session, Initialized, handle, "details", budget_tokens: 0);

        Assert.Equal("budget_tokens must be at least 1.", result);
    }

    [Fact]
    public void Expand_DetailsView_BudgetTokensIntMaxValue_ReturnsAllRemainingTextWithoutCrashingOrContinuing()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        // Larger than a default-budget page (800 tokens ≈ 2,880 chars) but under the
        // 2,000-token details ceiling, so Remember accepts it.
        var details = new string('w', 5000);
        var handle = HandleOf(EngramMcpTools.Remember(sandbox.Home, session, Initialized, "s", details: details));
        var fullText = "s\n\n" + details;

        var result = EngramMcpTools.Expand(
            sandbox.Home, session, Initialized, handle, "details", budget_tokens: int.MaxValue);

        Assert.Equal(fullText, result);
        Assert.DoesNotContain("continue with offset", result);
    }

    // The paging seam: every page's content, minus its continuation line, concatenated in
    // order, must reconstruct the source exactly — nothing lost, nothing doubled. The forward-
    // progress assertion inside the loop is what would catch a page that cuts back to its own
    // starting offset and never advances.
    [Fact]
    public void Expand_DetailsView_PagingThroughToTheEnd_ReconstructsTheFullTextExactly()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var wordsBuilder = new System.Text.StringBuilder();
        for (var i = 0; i < 500; i++)
        {
            if (i > 0)
            {
                wordsBuilder.Append(' ');
            }

            wordsBuilder.Append("word").Append(i);
        }

        var details = wordsBuilder.ToString();
        var handle = HandleOf(EngramMcpTools.Remember(sandbox.Home, session, Initialized, "s", details: details));
        var fullText = "s\n\n" + details;

        var rebuilt = new System.Text.StringBuilder();
        var offset = 0;
        var pages = 0;

        while (true)
        {
            pages++;
            Assert.True(pages < 200, "paging did not terminate — a page made no forward progress");

            var page = EngramMcpTools.Expand(
                sandbox.Home, session, Initialized, handle, "details", budget_tokens: 50, offset: offset);
            var marker = page.IndexOf("\n\nshowing chars", StringComparison.Ordinal);

            if (marker < 0)
            {
                rebuilt.Append(page);
                break;
            }

            rebuilt.Append(page[..marker]);
            var match = System.Text.RegularExpressions.Regex.Match(page, @"continue with offset: (\d+)");
            Assert.True(match.Success, $"expected a continuation line, got: {page}");
            var nextOffset = int.Parse(match.Groups[1].Value);

            Assert.True(nextOffset > offset, "paging must always move forward");
            offset = nextOffset;
        }

        Assert.Equal(fullText, rebuilt.ToString());
        Assert.True(pages > 1, "test is only meaningful if it actually paged");
    }

    [Fact]
    public void Recall_ALongTermFactWithA2000CharBody_PacksTruncatedWithAMarkerAndExpandsToTheFullBody()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var bodyBuilder = new System.Text.StringBuilder("zebrafinch flight pattern research notes");
        while (bodyBuilder.Length < 2000)
        {
            bodyBuilder.Append(" filler");
        }

        var body = bodyBuilder.ToString();
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = FactStore.Remember(
                connection,
                new FactWrite("/knowledge/testing/long-body-fact", "note", "states", body, "project", "stated"),
                DateTimeOffset.UtcNow).FactId;
        }

        var handle = $"f{factId}";

        var result = EngramMcpTools.Recall(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "zebrafinch flight pattern research");

        Assert.Contains($"[{handle}]", result);
        Assert.Contains("…", result);
        Assert.Matches(@"· \+[\d.]+k?\)", result);
        Assert.DoesNotContain(body, result);

        var expanded = EngramMcpTools.Expand(sandbox.Home, session, Initialized, handle, "details");
        Assert.Equal(body, expanded);
    }

    [Fact]
    public void Recall_ALongTermFactWithDetails_ShowsTheWholeBodyWithAMarkerSizedToDetails()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var body = "The kestrel migration route crosses three flyways.";
        var details = new string('d', 1500);

        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = FactStore.Remember(
                connection,
                new FactWrite(
                    "/knowledge/testing/kestrel-migration", "note", "states", body, "project", "stated",
                    Details: details),
                DateTimeOffset.UtcNow).FactId;
        }

        var handle = $"f{factId}";

        var result = EngramMcpTools.Recall(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "kestrel migration route flyways");

        Assert.Contains($"[{handle}] {body}", result);
        Assert.Contains("· +1.5k)", result);
    }

    [Fact]
    public void Remember_WithDetails_TheSessionLineIsMarkedAndExpandReturnsTheDepth()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "The nightly job now also verifies checksums.",
            details: "Verification uses SHA-256 and compares against the manifest written at backup time."));

        var result = EngramMcpTools.Recall(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "nightly job verifies checksums");
        var line = result.Split('\n').Single(l => l.Contains($"[{handle}]"));

        Assert.StartsWith($"[{handle}] The nightly job now also verifies checksums.", line);
        Assert.Matches(@"· \+\d+\)$", line);

        var expanded = EngramMcpTools.Expand(sandbox.Home, session, Initialized, handle, "details");
        Assert.Equal(
            "The nightly job now also verifies checksums.\n\n"
                + "Verification uses SHA-256 and compares against the manifest written at backup time.",
            expanded);
    }

    [Fact]
    public void Remember_WithoutDetails_TheSessionLineCarriesNoMarker()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-a");

        var handle = HandleOf(EngramMcpTools.Remember(
            sandbox.Home, session, Initialized, "The build pipeline retries flaky uploads three times before failing."));

        var result = EngramMcpTools.Recall(
            sandbox.Home, session, Initialized, NoRuntime(sandbox.Home), "build pipeline retries flaky uploads");
        var line = result.Split('\n').Single(l => l.Contains($"[{handle}]"));

        Assert.Equal($"[{handle}] The build pipeline retries flaky uploads three times before failing. (session)", line);
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

    /// <summary>
    /// engram_index_repo had no test driving it through the MCP surface at all — every existing
    /// assertion about enrollment rode through the CLI's RepoCommand instead, even though the two
    /// share ApplyDecision (D1) and nothing had confirmed the MCP entry point actually reaches it.
    /// </summary>
    [Fact]
    public void IndexRepo_EnrollDecision_RecordsEnrolledAndReportsSuccess()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-index-repo");
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        Directory.CreateDirectory(root);
        if (!GitInit(root))
        {
            return;
        }

        var response = EngramMcpTools.IndexRepo(sandbox.Home, session, root, "enroll");

        Assert.Contains("Enrolled", response, StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var row = Assert.Single(RepoEnrollment.ListAll(connection));
        Assert.Equal(RepoEnrollmentState.Enrolled, row.State);
    }

    [Fact]
    public void IndexRepo_APathWithNoEnclosingCheckout_RefusesWithoutRecording()
    {
        using var sandbox = new SandboxHome();
        var session = new McpSessionId("session-index-repo-refuse");
        var bare = Path.Combine(sandbox.Home.Root, "not-a-checkout");
        Directory.CreateDirectory(bare);

        var response = EngramMcpTools.IndexRepo(sandbox.Home, session, bare, "enroll");

        Assert.Contains("not inside a git checkout", response, StringComparison.Ordinal);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM repo_enrollment;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    private static bool GitInit(string directory)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("init");
            info.ArgumentList.Add("-q");

            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
