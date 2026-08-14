using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

public class HookSessionStartTests
{
    /// <summary>
    /// The session-start records, and only those.
    /// </summary>
    /// <remarks>
    /// telemetry.jsonl is a shared log and these tests are about one hook. Session start also
    /// spawns the maintenance child, which records its own indexing, so counting every line
    /// asserts something about the whole file instead. That count was already a race the
    /// assertions never meant to include — the child is detached, so how many of its records have
    /// landed by the time this reads is a matter of machine load.
    /// </remarks>
    private static IReadOnlyList<JsonElement> SessionStartRecords(TestHome home) =>
        File.ReadAllLines(Path.Combine(home.Root, "telemetry.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(record => record.GetProperty("kind").GetString() == "session-start")
            .ToList();

    [Fact]
    public void SessionStart_ExitsZero_EmitsValidJsonContract_PrimerUnder300Tokens()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var doc = JsonDocument.Parse(stdout);
        var hookSpecificOutput = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hookSpecificOutput.GetProperty("hookEventName").GetString());

        var primer = hookSpecificOutput.GetProperty("additionalContext").GetString();
        Assert.False(string.IsNullOrWhiteSpace(primer));

        var estimatedTokens = (int)Math.Ceiling(primer!.Length / 3.6);
        Assert.True(estimatedTokens <= 300, $"primer was {estimatedTokens} estimated tokens, expected <= 300");
    }

    // A store that cannot be read must produce silence, not the built-in corpus. Falling
    // back to CannedFacts would restore the divergence that moving the primer onto the
    // store removed, and would do it at the worst possible moment — telling someone who
    // forgot something that it is still remembered.
    //
    // This has to run out of process. Microsoft.Data.Sqlite pools connections, so the same
    // check inside the test host reads the corrupted file from a pooled connection's page
    // cache and reports the full corpus. A hook is a fresh process with an empty pool,
    // which is the situation being tested.
    [Fact]
    public void SessionStart_UnreadableDatabase_AnnouncesNothingRatherThanTheBuiltInCorpus()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var databasePath = Path.Combine(home.Root, "engram.db");
        Assert.True(File.Exists(databasePath), "the test home should have been initialised with a database");

        foreach (var sidecar in Directory.GetFiles(home.Root, "engram.db-*"))
        {
            File.Delete(sidecar);
        }

        File.WriteAllText(databasePath, "this is not a SQLite database");

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain("Memory holds", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStart_NoStdinData_StillExitsZero_AndTelemetryRecordHasNonEmptySessionId()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "hook", "session-start");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var sessionId = Assert.Single(SessionStartRecords(home)).GetProperty("session_id").GetString();

        Assert.False(string.IsNullOrEmpty(sessionId));
    }

    [Fact]
    public void SessionStart_DifferentStdinSessionIds_ProduceTwoTelemetryRecordsWithThoseTwoIds()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var first = EngramProcess.RunWithStdin(home.Root, """{"session_id":"session-aaa"}""", "hook", "session-start");
        var second = EngramProcess.RunWithStdin(home.Root, """{"session_id":"session-bbb"}""", "hook", "session-start");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);

        var records = SessionStartRecords(home);
        Assert.Equal(2, records.Count);

        var sessionIds = records
            .Select(record => record.GetProperty("session_id").GetString())
            .ToList();

        Assert.Equal(["session-aaa", "session-bbb"], sessionIds);
    }

    // The primer reaches every session whether or not the model calls a tool, so a record that
    // omits it makes `recall` the only visible read path — and recall is opt-in. That is the
    // measurement D6's gate on M3 and D18's on M4 both need, and neither could be read off the
    // 54 session-start records this instance had accumulated with every memory field null.
    [Fact]
    public void SessionStart_RecordsWhatThePrimerDelivered_NotMerelyThatOneStarted()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, _) = EngramProcess.Run(home.Root, "hook", "session-start");
        Assert.Equal(0, exitCode);

        var primer = JsonDocument.Parse(stdout).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();
        Assert.False(string.IsNullOrWhiteSpace(primer));

        var record = Assert.Single(SessionStartRecords(home));

        var longTerm = record.GetProperty("long_term_fact_count");
        Assert.NotEqual(JsonValueKind.Null, longTerm.ValueKind);
        Assert.True(longTerm.GetInt32() > 0, "the seeded home holds facts, so the primer reported some");

        var tokens = record.GetProperty("tokens_returned");
        Assert.NotEqual(JsonValueKind.Null, tokens.ValueKind);
        Assert.InRange(tokens.GetInt32(), 1, 300);
    }

    // fact_count means "facts returned to the model" on a recall record. A primer returns a count
    // line and up to two example bodies, which is not that — and filling the field with something
    // almost-right is how the probe came to subtract two disjoint session counts from each other
    // (D43). Null is the honest value and it has to stay null.
    [Fact]
    public void SessionStart_LeavesFactCountNull_BecauseAPrimerReturnsNoFacts()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        Assert.Equal(0, EngramProcess.Run(home.Root, "hook", "session-start").ExitCode);

        var record = Assert.Single(SessionStartRecords(home));

        Assert.Equal(JsonValueKind.Null, record.GetProperty("fact_count").ValueKind);
    }

    // §6.13's cwd fallback (payload?.Cwd ?? Directory.GetCurrentDirectory()) only matters when
    // the two disagree, so this drives the real binary with a process working directory that is
    // deliberately not a checkout — the primer can only find one by reading the stdin payload.
    // If it read the process cwd instead, FindCheckoutRoot(workingDirectory) resolves null and
    // there is no enrollment line at all, so this cannot pass for the wrong reason. Store state
    // (e.g. last_root) is not asserted here: the hook spawns a detached maintenance child, and
    // while stdout cannot race that child, store writes can.
    [Fact]
    public void SessionStart_OffersEnrollment_ForTheCheckoutNamedInStdin_NotTheProcessCwd()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var notACheckout = Path.Combine(Path.GetTempPath(), "engram-e2e-notacheckout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(notACheckout);

        var checkoutPath = Path.Combine(Path.GetTempPath(), "engram-e2e-checkout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(checkoutPath, ".git"));

        try
        {
            var stdin = JsonSerializer.Serialize(new { cwd = checkoutPath });
            var (exitCode, stdout, stderr) = EngramProcess.RunWithStdinFromDirectory(
                home.Root, notACheckout, stdin, "hook", "session-start");

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr);

            var primer = JsonDocument.Parse(stdout).RootElement
                .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();

            Assert.Contains("not enrolled for Engram code indexing", primer, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(notACheckout, recursive: true);
            Directory.Delete(checkoutPath, recursive: true);
        }
    }

    /// <summary>
    /// The guard for §5.1's structural gap: a session-start maintenance spawn that dies before it
    /// stamps <c>last_full_scan_at</c> leaves a repo neglected forever after, because every later
    /// session's own <c>--drain-all --auto</c> only ever touches the invoking root. <c>--freshen</c>
    /// exists to self-heal exactly this, from a session that starts somewhere else entirely — so
    /// this drives the published binary end to end rather than asserting through the code under
    /// test, and polls for the detached child's result rather than racing it.
    /// </summary>
    [Fact]
    public void SessionStart_SelfHeals_ANeglectedRepo_StartedFromADifferentCheckout()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var neglectedRepo = Path.Combine(home.Root, "neglected");
        Directory.CreateDirectory(neglectedRepo);
        File.WriteAllText(Path.Combine(neglectedRepo, "a.cs"), "class A {}\n");
        if (!GitInit(neglectedRepo))
        {
            return;
        }

        var otherCheckout = Path.Combine(
            Path.GetTempPath(), "engram-e2e-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(otherCheckout, ".git"));

        try
        {
            var (enrollExit, enrollOut, _) = EngramProcess.Run(home.Root, "repo", "enroll", neglectedRepo);
            Assert.Equal(0, enrollExit);
            Assert.Contains("first index is running in the background", enrollOut, StringComparison.Ordinal);

            var indexDeadline = DateTime.UtcNow.AddSeconds(30);
            var listOutput = string.Empty;
            while (!listOutput.Contains("file(s) indexed", StringComparison.Ordinal)
                   || listOutput.Contains("0 file(s) indexed", StringComparison.Ordinal))
            {
                if (DateTime.UtcNow >= indexDeadline)
                {
                    break;
                }

                Thread.Sleep(200);
                (_, listOutput, _) = EngramProcess.Run(home.Root, "repo", "list");
            }

            Assert.Contains("1 file(s) indexed", listOutput, StringComparison.Ordinal);

            // Simulate the dead spawn: the enrollment survives, but the scan that would have
            // stamped completion never landed.
            var databasePath = Path.Combine(home.Root, "engram.db");
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE repo_enrollment SET last_full_scan_at = NULL;";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var stdin = JsonSerializer.Serialize(new { cwd = otherCheckout });
            var (exitCode, _, stderr) = EngramProcess.RunWithStdinFromDirectory(
                home.Root, otherCheckout, stdin, "hook", "session-start");

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr);

            var healDeadline = DateTime.UtcNow.AddSeconds(30);
            long? stampAfter = null;
            while (stampAfter is null && DateTime.UtcNow < healDeadline)
            {
                Thread.Sleep(200);
                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT last_full_scan_at FROM repo_enrollment;";
                var result = command.ExecuteScalar();
                stampAfter = result is null or DBNull ? null : Convert.ToInt64(result);
            }

            Assert.NotNull(stampAfter);
        }
        finally
        {
            Directory.Delete(otherCheckout, recursive: true);
        }
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
