using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

[Collection(ConsoleStdinCollection.Name)]
public class LookupNudgeHookTests
{
    private const string Identity = "stamped-repo-identity";

    private static string BuildPayload(string? sessionId, string? toolName, string? pattern, string? command, string cwd)
    {
        var toolInput = new JsonObject();
        if (pattern is not null)
        {
            toolInput["pattern"] = pattern;
        }

        if (command is not null)
        {
            toolInput["command"] = command;
        }

        var root = new JsonObject { ["tool_input"] = toolInput, ["cwd"] = cwd };
        if (toolName is not null)
        {
            root["tool_name"] = toolName;
        }

        if (sessionId is not null)
        {
            root["session_id"] = sessionId;
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// A checkout the hook can find by walking up from <c>cwd</c>: a directory holding a
    /// <c>.git</c> marker, with a subdirectory to start from so the walk is actually exercised.
    /// </summary>
    private static (string Root, string Cwd) Checkout(SandboxHome sandbox)
    {
        var root = Path.Combine(sandbox.Home.Root, "checkout");
        var cwd = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(cwd);
        return (root, cwd);
    }

    /// <summary>The state every deny case needs: enrolled, then indexed at least once.</summary>
    private static (string Root, string Cwd) IndexedCheckout(SandboxHome sandbox)
    {
        var checkout = Checkout(sandbox);
        var now = DateTimeOffset.UtcNow;
        RepoIndexStamp.Append(sandbox.Home.RepoIndexStampPath, now, checkout.Root, Identity, "enroll");
        RepoIndexStamp.Append(sandbox.Home.RepoIndexStampPath, now, checkout.Root, Identity, RepoIndexStamp.Indexed);
        return checkout;
    }

    // Console.SetIn is process-global, not per-test — see ConsoleStdinCollection.
    private static (int ExitCode, string Stdout, string Stderr) Run(
        SandboxHome sandbox, string cwd, string? sessionId, string? toolName, string? pattern = null, string? command = null)
    {
        Console.SetIn(new StringReader(BuildPayload(sessionId, toolName, pattern, command, cwd)));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "lookup-nudge"], stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static IReadOnlyList<TelemetryRecord> LookupNudgeTelemetryRecords(SandboxHome sandbox)
    {
        var path = Telemetry.ResolvePath(sandbox.Home);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(Telemetry.TryParse)
            .Where(record => record is not null && record.Kind == TelemetryEventKind.LookupNudge)
            .Select(record => record!)
            .ToList();
    }

    private static void AssertDeny(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            "deny",
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    private static void AssertSilentAndUnspent(SandboxHome sandbox, (int ExitCode, string Stdout, string Stderr) result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.False(File.Exists(sandbox.Home.LookupNudgeStatePath));
        Assert.Empty(LookupNudgeTelemetryRecords(sandbox));
    }

    [Fact]
    public void SymbolShapedGrep_Denies()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        var (exitCode, stdout, stderr) = Run(sandbox, cwd, "session-a", "Grep", pattern: "ProcessFile");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var doc = JsonDocument.Parse(stdout);
        var hookSpecificOutput = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", hookSpecificOutput.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", hookSpecificOutput.GetProperty("permissionDecision").GetString());

        var reason = hookSpecificOutput.GetProperty("permissionDecisionReason").GetString();
        Assert.Contains("engram_navigate", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainWordGrep_EmptyStdoutAndNoState()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        var (exitCode, stdout, stderr) = Run(sandbox, cwd, "session-b", "Grep", pattern: "latency");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.False(File.Exists(sandbox.Home.LookupNudgeStatePath));
    }

    [Fact]
    public void ShellGrep_Denies()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        var (exitCode, stdout, stderr) = Run(sandbox, cwd, "session-c", "Bash", command: "grep -rn ProcessFile src/");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        AssertDeny(stdout);
    }

    [Fact]
    public void NonSearchBash_EmptyStdout()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        var (exitCode, stdout, stderr) = Run(sandbox, cwd, "session-d", "Bash", command: "dotnet test");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void SecondCallSameSession_EmptyStdout()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        // The first call must be asserted to DENY, not merely run. Asserting only that the second
        // is silent passes just as well when the hook never fired at all, which is what a broken
        // classifier looks like — the once-per-session rule would then be untested by the one test
        // named after it.
        var first = Run(sandbox, cwd, "session-e", "Grep", pattern: "ProcessFile");
        AssertDeny(first.Stdout);

        var (exitCode, stdout, stderr) = Run(sandbox, cwd, "session-e", "Grep", pattern: "ProcessFile");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void DifferentSession_DeniesAgain()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        Run(sandbox, cwd, "session-f1", "Grep", pattern: "ProcessFile");
        var (exitCode, stdout, _) = Run(sandbox, cwd, "session-f2", "Grep", pattern: "ProcessFile");

        Assert.Equal(0, exitCode);
        AssertDeny(stdout);
    }

    [Fact]
    public void PrecedenceOff_EmptyStdout()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);
        File.AppendAllText(sandbox.Home.ConfigPath, "\n[memory]\nprecedence = \"off\"\n");

        var (exitCode, stdout, stderr) = Run(sandbox, cwd, "session-g", "Grep", pattern: "ProcessFile");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void Deny_WritesLookupNudgeTelemetryRecord_WithPhaseAndRepo()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        Run(sandbox, cwd, "session-h", "Grep", pattern: "ProcessFile");

        // Filter by phase, not just kind: a lookup-nudge line is one end of the event, and a
        // reader counting lines would double-count a nudge that was later overridden.
        var nudged = LookupNudgeTelemetryRecords(sandbox).Where(r => r.Phase == HookCommand.LookupNudgePhaseNudged).ToList();
        var record = Assert.Single(nudged);
        Assert.Equal("session-h", record.SessionId);
        Assert.Equal("ProcessFile", record.Query);
        Assert.Equal(Identity, record.Repo);
    }

    private static IReadOnlyList<TelemetryRecord> Phase(SandboxHome sandbox, string phase) =>
        LookupNudgeTelemetryRecords(sandbox).Where(r => r.Phase == phase).ToList();

    // Compliance (code-nav-adoption-spec L6): the deny's own escape hatch — re-run the exact query —
    // is the one behaviour counted as an override, once, inside the hook's own id space.

    [Fact]
    public void RerunOfDeniedQuery_ProceedsAndWritesOneOverriddenRecord_WithSameRepo()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        AssertDeny(Run(sandbox, cwd, "session-n", "Grep", pattern: "ProcessFile").Stdout);
        var (exitCode, stdout, stderr) = Run(sandbox, cwd, "session-n", "Bash", command: "grep -rn ProcessFile src/");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);

        var nudged = Assert.Single(Phase(sandbox, HookCommand.LookupNudgePhaseNudged));
        var overridden = Assert.Single(Phase(sandbox, HookCommand.LookupNudgePhaseOverridden));
        Assert.Equal("session-n", overridden.SessionId);
        Assert.Equal("ProcessFile", overridden.Query);
        Assert.Equal(nudged.Repo, overridden.Repo);
    }

    [Fact]
    public void SecondRerunOfDeniedQuery_WritesNothingMore()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        AssertDeny(Run(sandbox, cwd, "session-o", "Grep", pattern: "ProcessFile").Stdout);
        Run(sandbox, cwd, "session-o", "Grep", pattern: "ProcessFile");
        var (_, stdout, _) = Run(sandbox, cwd, "session-o", "Grep", pattern: "ProcessFile");

        Assert.Equal(string.Empty, stdout);
        Assert.Single(Phase(sandbox, HookCommand.LookupNudgePhaseOverridden));
    }

    [Fact]
    public void DifferentSymbolAfterNudge_ProceedsAndWritesNothing()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = IndexedCheckout(sandbox);

        AssertDeny(Run(sandbox, cwd, "session-p", "Grep", pattern: "ProcessFile").Stdout);
        var (exitCode, stdout, _) = Run(sandbox, cwd, "session-p", "Grep", pattern: "ReadPayload");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(Phase(sandbox, HookCommand.LookupNudgePhaseOverridden));
        Assert.Single(Phase(sandbox, HookCommand.LookupNudgePhaseNudged));
    }

    // The gate (code-nav-adoption-spec L1). Every case below is a symbol-shaped Grep that would
    // deny on an indexed checkout; what varies is only what the stamp says about this one. Each
    // asserts the shot is unspent as well as silent — a gate that swallowed the shot would
    // reproduce the exact failure it exists to remove.

    [Fact]
    public void NoStampForCheckout_SilentAndShotUnspent()
    {
        using var sandbox = new SandboxHome();
        var (_, cwd) = Checkout(sandbox);

        AssertSilentAndUnspent(sandbox, Run(sandbox, cwd, "session-i", "Grep", pattern: "ProcessFile"));
    }

    [Fact]
    public void EnrolledButNeverIndexed_SilentAndShotUnspent()
    {
        using var sandbox = new SandboxHome();
        var (root, cwd) = Checkout(sandbox);
        RepoIndexStamp.Append(sandbox.Home.RepoIndexStampPath, DateTimeOffset.UtcNow, root, Identity, "enroll");

        AssertSilentAndUnspent(sandbox, Run(sandbox, cwd, "session-j", "Grep", pattern: "ProcessFile"));
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("later")]
    public void DeclinedOrDeferred_SilentAndShotUnspent_EvenIfPreviouslyIndexed(string decision)
    {
        using var sandbox = new SandboxHome();
        var (root, cwd) = IndexedCheckout(sandbox);
        RepoIndexStamp.Append(sandbox.Home.RepoIndexStampPath, DateTimeOffset.UtcNow, root, Identity, decision);

        AssertSilentAndUnspent(sandbox, Run(sandbox, cwd, "session-k", "Grep", pattern: "ProcessFile"));
    }

    [Fact]
    public void CwdOutsideAnyCheckout_SilentAndShotUnspent()
    {
        using var sandbox = new SandboxHome();
        IndexedCheckout(sandbox);
        var elsewhere = Path.Combine(sandbox.Home.Root, "not-a-checkout");
        Directory.CreateDirectory(elsewhere);

        AssertSilentAndUnspent(sandbox, Run(sandbox, elsewhere, "session-l", "Grep", pattern: "ProcessFile"));
    }

    [Fact]
    public void UnindexedCheckoutDoesNotSpendTheShot_IndexedOneStillDenies()
    {
        using var sandbox = new SandboxHome();
        var (_, indexedCwd) = IndexedCheckout(sandbox);
        var other = Path.Combine(sandbox.Home.Root, "other");
        Directory.CreateDirectory(Path.Combine(other, ".git"));

        AssertSilentAndUnspent(sandbox, Run(sandbox, other, "session-m", "Grep", pattern: "ProcessFile"));

        var (_, stdout, _) = Run(sandbox, indexedCwd, "session-m", "Grep", pattern: "ProcessFile");
        AssertDeny(stdout);
    }
}
