using System.Text.Json;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class HookCommandTests
{
    [Fact]
    public void SessionStart_ExitsZero_EmitsSessionStartJson()
    {
        using var sandbox = new SandboxHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        using var doc = JsonDocument.Parse(stdout.ToString());
        var hookSpecificOutput = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hookSpecificOutput.GetProperty("hookEventName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(hookSpecificOutput.GetProperty("additionalContext").GetString()));
    }

    // The whole reason the primer moved onto the store: a forgotten fact has to stop being
    // announced. Reading a hardcoded list cannot express this, and would keep telling the
    // user about memory they explicitly cleared.
    [Fact]
    public void SessionStart_AfterEverythingWasForgotten_AnnouncesNoFacts()
    {
        using var sandbox = new SandboxHome();

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            foreach (var fact in FactStore.ReadLive(connection))
            {
                FactStore.Forget(connection, fact.Id, "user cleared memory", DateTimeOffset.UtcNow);
            }
        }

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Memory holds", stdout.ToString(), StringComparison.Ordinal);
    }

    // The topic names the primer prints are display text the corpus was authored with, and
    // the path only carries a slug. Asserting on the rendered primer rather than on the
    // catalog, because a slug leaking here is what the model would actually read.
    [Fact]
    public void SessionStart_PrintsTopicsAsAuthored_NotAsSlugs()
    {
        using var sandbox = new SandboxHome();
        var stdout = new StringWriter();

        CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], stdout, new StringWriter());

        var output = stdout.ToString();
        Assert.Contains("claude-code hooks", output, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-code-hooks", output, StringComparison.Ordinal);
    }

    // The matching "unreadable database" case is an end-to-end test, not one of these.
    // Microsoft.Data.Sqlite pools connections per process, so corrupting the file here is
    // invisible: the read is served from a pooled connection that still has the old pages
    // cached, and the hook cheerfully reports all 45 facts. Checked — that is how the first
    // version of this test passed while proving nothing.

    [Fact]
    public void SessionStart_MissingHomeDirectory_ExitsZeroAndWritesNothing()
    {
        // initialize: false, so the deleted directory never held a database whose pooled
        // handle would make the delete itself throw on Windows.
        using var sandbox = new SandboxHome(initialize: false);
        Directory.Delete(sandbox.Home.Root, recursive: true);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void SessionStart_InitialisedHome_RecordsTelemetry()
    {
        using var sandbox = new SandboxHome();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "session-start"], new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
        var telemetryPath = Path.Combine(sandbox.Home.Root, "telemetry.jsonl");
        Assert.True(File.Exists(telemetryPath));
        Assert.Contains("\"kind\":\"session-start\"", File.ReadAllText(telemetryPath));
    }

    [Fact]
    public void UninitialisedHome_AllHookEvents_ExitZeroSilentlyAndCreateNoFiles()
    {
        using var sandbox = new SandboxHome(initialize: false);

        foreach (var eventName in new[] { "session-start", "file-touched", "pre-compact", "post-compact" })
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", eventName], stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }

        Assert.Empty(Directory.GetFileSystemEntries(sandbox.Home.Root));
    }

    [Fact]
    public void PreCompact_ExitsZero_EmitsDigestInstructionOnBareStdout()
    {
        using var sandbox = new SandboxHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "pre-compact"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        var output = stdout.ToString();

        // hookSpecificOutput is measured to be REJECTED on this event, so a habit-reflex
        // WriteJson here would silently break the channel; this runs first so it gives a
        // clearer failure message than the equality check below would on that regression.
        Assert.False(output.TrimStart().StartsWith('{'), "PreCompact stdout must be bare, not a JSON envelope");

        // Nothing else may write to this channel: contains-only checks would still pass if
        // something appended a JSON envelope after the bare instruction.
        Assert.Equal(CompactionDigest.Instruction + "\n", output);
    }

    [Fact]
    public void FileTouched_ExitsZero_CreatesOneSpoolFilePerInvocation()
    {
        using var sandbox = new SandboxHome();

        for (var i = 0; i < 5; i++)
        {
            var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "file-touched"], new StringWriter(), new StringWriter());
            Assert.Equal(0, exitCode);
        }

        var spoolFiles = Directory.GetFiles(sandbox.Home.QueueDir);
        Assert.Equal(5, spoolFiles.Length);
    }

    [Fact(Timeout = 300_000)]
    public async Task FileTouched_FiftyConcurrentInProcessCalls_ProduceFiftyDistinctSpoolFiles()
    {
        using var sandbox = new SandboxHome();

        var runs = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => CliApp.Run(["--home", sandbox.Home.Root, "hook", "file-touched"], new StringWriter(), new StringWriter())));
        var exitCodes = await Task.WhenAll(runs);

        Assert.All(exitCodes, code => Assert.Equal(0, code));

        var spoolFiles = Directory.GetFiles(sandbox.Home.QueueDir);
        Assert.Equal(50, spoolFiles.Length);

        var distinctNames = spoolFiles.Select(Path.GetFileName).Distinct().Count();
        Assert.Equal(50, distinctNames);
    }

    [Fact]
    public void FileTouched_MissingHomeDirectory_DoesNotThrow()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Directory.Delete(sandbox.Home.Root, recursive: true);

        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "hook", "file-touched"], new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
    }

    // §6.13's fallback (payload?.Cwd ?? Directory.GetCurrentDirectory()) means a misspelled
    // JsonPropertyName on Cwd fails silently in production — the process cwd is usually right
    // anyway, so nothing would surface it outside the one case §6.13 exists to handle.
    [Fact]
    public void HookStdinInput_DeserializesCwd_FromThePayload()
    {
        var payload = JsonSerializer.Deserialize(
            """{"session_id":"s","cwd":"/x"}""",
            HookJsonContext.Default.HookStdinInput);

        Assert.Equal("/x", payload?.Cwd);
    }
}
