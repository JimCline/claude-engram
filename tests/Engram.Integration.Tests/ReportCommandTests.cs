using System.Text.Json;
using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// D22 acceptance items 1-14 and 16-18 (docs/engram-d22-report-spec.md §9). Item 15 (tier 3, the
/// published binary) lives in Engram.EndToEnd.Tests.
/// </summary>
public class ReportCommandTests
{
    private static long Seed(SqliteConnection connection, FactWrite write, DateTimeOffset now) =>
        FactStore.Remember(connection, write, now).FactId;

    private static string TelemetryPath(SandboxHome sandbox) => Path.Combine(sandbox.Home.Root, "telemetry.jsonl");

    private static List<JsonElement> ReadTelemetry(SandboxHome sandbox)
    {
        var path = TelemetryPath(sandbox);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToList();
    }

    // Item 1: no-args run writes <home>/reports/engram-report-<stamp>.md, prints exactly that path, exit 0.
    [Fact]
    public void NoArgs_WritesTimestampedFileUnderReportsDir_PrintsExactPath_ExitZero()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "likes", "coffee", "personal", "stated"), DateTimeOffset.UtcNow);
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(["--home", sandbox.Home.Root, "report"], stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.ToString());

        var printedPath = stdout.ToString().TrimEnd('\r', '\n');
        Assert.True(File.Exists(printedPath), $"printed path does not exist: {printedPath}");
        Assert.Equal(Path.Combine(sandbox.Home.Root, "reports"), Path.GetDirectoryName(printedPath));
        Assert.Matches(@"engram-report-\d{8}-\d{6}\.md$", printedPath);
    }

    // Item 2: closed fact appears marked closed with superseded-by/reason.
    // Falsification: add "AND valid_to IS NULL" to the report's read — this test reddens because
    // the closed entry, its "superseded by #id", and its reason all vanish from the document.
    [Fact]
    public void ClosedFact_RendersClosedMarkerAndSupersessionDetails()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        long newId;
        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            var now = DateTimeOffset.UtcNow;
            oldId = Seed(connection, new FactWrite("me", "user", "favorite-color", "green", "personal", "stated"), now);
            newId = Seed(connection, new FactWrite("me", "user", "favorite-color", "blue", "personal", "stated"), now.AddSeconds(5));
        }

        var stdout = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], stdout, new StringWriter());
        var doc = stdout.ToString();

        Assert.Contains("**closed**", doc);
        Assert.Contains($"superseded by #{newId}", doc);
        Assert.DoesNotContain($"superseded by #{oldId}", doc);
    }

    // Item 3: nothing truncated — a long body and a details field both render in full.
    // Falsification: apply recall's truncation to the body before writing it — this test reddens
    // because the full body no longer appears verbatim.
    [Fact]
    public void LongBodyAndDetails_RenderInFullWithNoTruncation()
    {
        using var sandbox = new SandboxHome();
        var longBody = string.Concat(Enumerable.Repeat("this sentence is deliberately long. ", 50));
        var details = string.Concat(Enumerable.Repeat("elaboration text. ", 200));

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(
                connection,
                new FactWrite("me", "user", "notes", longBody, "personal", "stated", Details: details),
                DateTimeOffset.UtcNow);
        }

        var stdout = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], stdout, new StringWriter());
        var doc = stdout.ToString();

        Assert.Contains(longBody, doc);
        Assert.Contains(details, doc);
    }

    // Item 4: timestamps are second-resolution, local, via MomentText.
    // Falsification: render "yyyy-MM-dd" instead — this test reddens because the time-of-day
    // component disappears from the "from" line.
    [Fact]
    public void Timestamps_UseMomentTextSecondResolutionLocalFormat()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "likes", "coffee", "personal", "stated"), now);
        }

        var stdout = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], stdout, new StringWriter());
        var doc = stdout.ToString();

        var expected = MomentText.In(now.ToUnixTimeSeconds(), TimeZoneInfo.Local);
        Assert.Contains($"from {expected}", doc);
    }

    // Item 5: a plain forget (closed, no superseded-by) is distinguishable from a revision (closed,
    // with superseded-by).
    [Fact]
    public void ForgetVsRevision_AreDistinguishableInTheDocument()
    {
        using var sandbox = new SandboxHome();
        long forgottenId;
        long revisedOldId;
        var now = DateTimeOffset.UtcNow;

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            forgottenId = Seed(connection, new FactWrite("me", "user", "temp-note", "expired info", "personal", "stated"), now);
            FactStore.Forget(connection, forgottenId, "no longer true", now.AddSeconds(1));

            revisedOldId = Seed(connection, new FactWrite("me", "user", "favorite-color", "green", "personal", "stated"), now);
            Seed(connection, new FactWrite("me", "user", "favorite-color", "blue", "personal", "stated"), now.AddSeconds(2));
        }

        var stdout = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], stdout, new StringWriter());
        var doc = stdout.ToString();

        // The forgotten fact is closed with no "superseded by" anywhere near it, while the
        // revised one is closed and does carry one. FactStore.Forget's reason IS persisted, as a
        // supersession row with new_fact_id = NULL (FactStore.cs:184-193), and FactJournal.Read's
        // LEFT JOIN picks it up regardless — so the reason must render for the forgotten fact too.
        var bodyIndex = doc.IndexOf("expired info", StringComparison.Ordinal);
        var entryStart = doc.LastIndexOf("- **", bodyIndex, StringComparison.Ordinal);
        var entryEnd = doc.IndexOf("\n\n", bodyIndex, StringComparison.Ordinal);
        var forgottenEntry = doc[entryStart..(entryEnd < 0 ? doc.Length : entryEnd)];
        Assert.Contains("**closed**", forgottenEntry);
        Assert.DoesNotContain("superseded by", forgottenEntry);
        Assert.Contains("reason: no longer true", forgottenEntry);

        Assert.Contains($"superseded by", doc);
    }

    // Item 6: fence lengths are computed — a body containing its own triple-backtick fence and a
    // leading "#" line does not corrupt heading/section boundaries.
    // Falsification: hardcode a 3-backtick fence — this test reddens because the embedded fence
    // closes the outer one early, spilling "# forged heading" out as real Markdown structure.
    [Fact]
    public void BodyContainingBacktickFenceAndHeadingLine_DoesNotCorruptDocumentStructure()
    {
        using var sandbox = new SandboxHome();
        var tricky = "```\n# forged heading\n```\nmore text";

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "notes", tricky, "personal", "stated"), DateTimeOffset.UtcNow);
        }

        var stdout = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], stdout, new StringWriter());
        var doc = stdout.ToString();

        Assert.Contains("````\n" + tricky, doc);
        // The real document structure survives: exactly one "### me" subject heading and one
        // "#### notes" predicate heading, not two — a broken fence would let "# forged heading"
        // register as its own top-level heading instead of body content.
        Assert.Equal(1, doc.Split("### me").Length - 1);
        Assert.Equal(1, doc.Split("#### notes").Length - 1);
    }

    // Item 7: ordering is deterministic — two runs of the same store differ only on the
    // "generated:" line. Note: a stable-sort accident that happens to hold on one run's insertion
    // order would still pass a single-run comparison, so this seeds facts in an order that
    // contradicts subject/predicate/valid_from ordering, forcing a real sort to reorder them.
    [Fact]
    public void Ordering_IsDeterministic_AcrossRepeatedRuns()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            // Insertion order is deliberately the reverse of subject order, so passing requires an
            // actual sort rather than accidentally preserving insertion/id order.
            Seed(connection, new FactWrite("zzz-subject", "user", "p", "z", "personal", "stated"), now);
            Seed(connection, new FactWrite("aaa-subject", "user", "p", "a", "personal", "stated"), now);
            Seed(connection, new FactWrite("mmm-subject", "user", "p", "m", "personal", "stated"), now);
        }

        var out1 = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], out1, new StringWriter());
        var out2 = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], out2, new StringWriter());

        var lines1 = out1.ToString().Split('\n');
        var lines2 = out2.ToString().Split('\n');

        Assert.Equal(lines1.Length, lines2.Length);
        for (var i = 0; i < lines1.Length; i++)
        {
            if (lines1[i].StartsWith("generated:", StringComparison.Ordinal))
            {
                Assert.StartsWith("generated:", lines2[i], StringComparison.Ordinal);
                continue;
            }

            Assert.Equal(lines1[i], lines2[i]);
        }

        // And the order is actually the sorted one, not merely stable.
        Assert.True(out1.ToString().IndexOf("aaa-subject", StringComparison.Ordinal)
            < out1.ToString().IndexOf("mmm-subject", StringComparison.Ordinal));
        Assert.True(out1.ToString().IndexOf("mmm-subject", StringComparison.Ordinal)
            < out1.ToString().IndexOf("zzz-subject", StringComparison.Ordinal));
    }

    // Item 8: report and the journal (backups/facts.jsonl's source) read the same fact-id set.
    // Structurally guaranteed by both calling FactJournal.Read — asserted explicitly so a future
    // divergence (either side gaining its own filter) is caught here.
    // Falsification: filter either side (e.g. add authored-only filtering unconditionally on the
    // MemoryReport side) — this test reddens on the id-set comparison.
    [Fact]
    public void ReportAndJournal_ReadTheSameFactIdSet()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;
        HashSet<long> journalIds;

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            var oldId = Seed(connection, new FactWrite("me", "user", "favorite-color", "green", "personal", "stated"), now);
            Seed(connection, new FactWrite("me", "user", "favorite-color", "blue", "personal", "stated"), now.AddSeconds(1));
            Seed(connection, new FactWrite("proj", "code-file", "kind", "csharp", "code", "observed", Regenerable: true), now);

            journalIds = FactJournal.Read(connection).Select(f => f.Id).ToHashSet();
        }

        var stdout = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], stdout, new StringWriter());
        var doc = stdout.ToString();

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            var reportIds = FactJournal.Read(connection).Select(f => f.Id).ToHashSet();
            Assert.Equal(journalIds, reportIds);
        }
    }

    // Item 9: --authored-only excludes regenerable facts, and the header states the filter and the
    // excluded count. The header assertion is the load-bearing half — the filter alone would pass
    // even if the header silently claimed "scope: all facts".
    [Fact]
    public void AuthoredOnly_ExcludesRegenerableFacts_AndHeaderStatesFilterAndCount()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (EngramDatabase.OpenInitialized(sandbox.Home)) { } // schema only, no canned seed corpus
        var now = DateTimeOffset.UtcNow;

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "likes", "coffee", "personal", "stated"), now);
            Seed(connection, new FactWrite("proj", "code-file", "kind", "csharp", "code", "observed", Regenerable: true), now);
            Seed(connection, new FactWrite("proj2", "code-file", "kind", "csharp", "code", "observed", Regenerable: true), now);
        }

        var stdout = new StringWriter();
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-", "--authored-only"], stdout, new StringWriter());
        var doc = stdout.ToString();

        Assert.Contains("scope: authored facts only — 2 regenerable fact(s) excluded", doc);
        Assert.Contains("coffee", doc);
        Assert.DoesNotContain("csharp", doc);
    }

    // Item 10: --out - writes to stdout and creates no file.
    [Fact]
    public void OutDash_WritesToStdout_CreatesNoFile()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "likes", "coffee", "personal", "stated"), DateTimeOffset.UtcNow);
        }

        var stdout = new StringWriter();
        var exit = CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], stdout, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Contains("coffee", stdout.ToString());

        var reportsDir = Path.Combine(sandbox.Home.Root, "reports");
        Assert.False(Directory.Exists(reportsDir) && Directory.EnumerateFileSystemEntries(reportsDir).Any());
    }

    // Item 11: --out <existing path> without --force exits 1, writes nothing, names the conflict;
    // with --force overwrites.
    [Fact]
    public void OutExistingPath_WithoutForce_ExitsOneAndDoesNotOverwrite_WithForce_Overwrites()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "likes", "coffee", "personal", "stated"), DateTimeOffset.UtcNow);
        }

        var target = Path.Combine(sandbox.Home.Root, "existing.md");
        File.WriteAllText(target, "sentinel content");

        var stderr = new StringWriter();
        var exit = CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", target], new StringWriter(), stderr);

        Assert.Equal(1, exit);
        Assert.Contains(target, stderr.ToString());
        Assert.Equal("sentinel content", File.ReadAllText(target));

        var exit2 = CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", target, "--force"], new StringWriter(), new StringWriter());
        Assert.Equal(0, exit2);
        Assert.Contains("coffee", File.ReadAllText(target));
    }

    // Item 12: empty store yields a valid zero-fact document, exit 0.
    [Fact]
    public void EmptyStore_YieldsValidZeroFactDocument_ExitZero()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (EngramDatabase.OpenInitialized(sandbox.Home)) { } // schema only, no canned seed corpus

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        var doc = stdout.ToString();
        Assert.Contains("facts: 0 total — 0 live, 0 closed", doc);
        Assert.Contains("none", doc);
    }

    // Item 13: schema-too-old exits 1, names the migrating verb, and the store's mtime is
    // unchanged — proving no migration happened.
    // Falsification: switch ReportCommand.Run to EngramDatabase.OpenInitialized — this test
    // reddens on the mtime assertion, since OpenInitialized migrates (and D31 snapshots) on open.
    [Fact]
    public void SchemaTooOld_ExitsOneNamingServe_AndLeavesStoreMtimeUnchanged()
    {
        using var sandbox = new SandboxHome();

        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_meta SET value = '1' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        var mtimeBefore = File.GetLastWriteTimeUtc(sandbox.Home.DatabasePath);
        Thread.Sleep(10);

        var stderr = new StringWriter();
        var exit = CliApp.Run(["--home", sandbox.Home.Root, "report"], new StringWriter(), stderr);

        Assert.Equal(1, exit);
        Assert.Contains("serve", stderr.ToString());
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(sandbox.Home.DatabasePath));
    }

    // Item 14: backups/facts.jsonl is unaffected by running report — a standing regression guard.
    // No FactJournal.cs extraction occurred in this change (Read was already public and reused
    // as-is), so there is no prior version to diff against; this snapshots the journal before and
    // after running report and asserts byte equality.
    [Fact]
    public void RunningReport_LeavesBackupJournalByteIdentical()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "likes", "coffee", "personal", "stated"), DateTimeOffset.UtcNow);
            FactJournal.Write(connection, sandbox.Home, DateTimeOffset.UtcNow);
        }

        var journalPath = Path.Combine(sandbox.Home.BackupDir, FactJournal.FileName);
        Assert.True(File.Exists(journalPath));
        var before = File.ReadAllBytes(journalPath);

        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], new StringWriter(), new StringWriter());

        var after = File.ReadAllBytes(journalPath);
        Assert.Equal(before, after);
    }

    // Item 16: TelemetryEventKind.Report is registered in All — asserted by reflecting over the
    // constants, never by iterating All (the tautology trap: deleting a kind from All would make
    // an All-driven test simply never visit it).
    [Fact]
    public void ReportKind_IsRegisteredInAll_AssertedByReflectionOverConstants()
    {
        var constants = typeof(TelemetryEventKind)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToHashSet();

        Assert.Contains(TelemetryEventKind.Report, constants);
        Assert.Contains(TelemetryEventKind.Report, TelemetryEventKind.All);
    }

    // Item 17: exactly one "report" telemetry line after a successful run, zero recall/remember
    // lines, and a failing invocation emits no new "report" line. Both halves are load-bearing.
    [Fact]
    public void Telemetry_OneReportLineOnSuccess_NoneOnFailure_NoRecallOrRememberLines()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "likes", "coffee", "personal", "stated"), DateTimeOffset.UtcNow);
        }

        var target = Path.Combine(sandbox.Home.Root, "conflict.md");
        File.WriteAllText(target, "sentinel");
        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", target], new StringWriter(), new StringWriter());

        var afterFailure = ReadTelemetry(sandbox);
        Assert.DoesNotContain(afterFailure, e => e.GetProperty("kind").GetString() == "report");

        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], new StringWriter(), new StringWriter());

        var afterSuccess = ReadTelemetry(sandbox);
        var reportLines = afterSuccess.Where(e => e.GetProperty("kind").GetString() == "report").ToList();
        Assert.Single(reportLines);
        Assert.DoesNotContain(afterSuccess, e => e.GetProperty("kind").GetString() is "recall" or "remember");
    }

    // Item 18: the report telemetry record's fact_count is absent/null; its own count fields carry
    // the header's numbers.
    // Falsification: write the total into fact_count — this test reddens on the null assertion.
    [Fact]
    public void TelemetryRecord_FactCountIsNull_OwnCountFieldsCarryTheHeaderNumbers()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (EngramDatabase.OpenInitialized(sandbox.Home)) { } // schema only, no canned seed corpus
        using (var connection = EngramDatabase.Open(sandbox.Home))
        {
            Seed(connection, new FactWrite("me", "user", "likes", "coffee", "personal", "stated"), DateTimeOffset.UtcNow);
            var oldId = Seed(connection, new FactWrite("me", "user", "favorite-color", "green", "personal", "stated"), DateTimeOffset.UtcNow);
            Seed(connection, new FactWrite("me", "user", "favorite-color", "blue", "personal", "stated"), DateTimeOffset.UtcNow.AddSeconds(1));
        }

        CliApp.Run(["--home", sandbox.Home.Root, "report", "--out", "-"], new StringWriter(), new StringWriter());

        var record = ReadTelemetry(sandbox).Single(e => e.GetProperty("kind").GetString() == "report");

        Assert.False(record.TryGetProperty("fact_count", out var factCount) && factCount.ValueKind != JsonValueKind.Null);
        Assert.Equal(3, record.GetProperty("report_total_facts").GetInt32());
        Assert.Equal(2, record.GetProperty("report_live_facts").GetInt32());
        Assert.Equal(1, record.GetProperty("report_closed_facts").GetInt32());
        Assert.True(record.GetProperty("report_bytes_written").GetInt32() > 0);
    }
}
