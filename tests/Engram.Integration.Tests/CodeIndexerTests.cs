using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class CodeIndexerTests
{
    private const string ProgramCs = """
        /// <summary>Turns cranks into torque.</summary>
        using System.Text;

        public sealed class Widget { }
        public enum Gear { Low }
        """;

    private const string ReadmeMd = """
        Fixture repo for the code indexer.

        # Guide

        ## Usage

        Run the fixture through the indexer.
        """;

    [Fact]
    public void FullIndex_WritesObservedRegenerableCodeFacts_AndARerunWritesNothing()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var report = Index(connection, sandbox, repo, apply: true);

        Assert.True(report.FactsWritten > 0);
        var facts = CodeFacts(connection);
        Assert.NotEmpty(facts);
        Assert.All(facts, fact =>
        {
            Assert.Equal("observed", fact.LearnedVia);
            Assert.True(fact.Regenerable, $"{fact.SubjectPath} {fact.Predicate} must be regenerable (D23)");
            Assert.Equal("code", fact.Scope);
        });

        Assert.Contains(facts, fact =>
            fact.SubjectPath.EndsWith("/Program.cs#Widget", StringComparison.Ordinal)
            && fact.Predicate == "declared-as");
        Assert.Contains(facts, fact =>
            fact.SubjectPath.EndsWith("/README.md#guide/usage", StringComparison.Ordinal));

        var again = Index(connection, sandbox, repo, apply: true);

        Assert.Equal(0, again.FactsWritten);
        Assert.Equal(0, again.FactsClosed);
        Assert.Equal(0, again.Analyzed);
        Assert.Equal(facts.Count, CodeFacts(connection).Count);
    }

    [Fact]
    public void EditedFile_ClosesWhatItNoLongerSays_AndTheHistoryStays()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            ProgramCs.Replace("public enum Gear { Low }", "public enum Sprocket { Fine }"));

        var report = Index(connection, sandbox, repo, apply: true);

        Assert.Equal(1, report.Analyzed);
        var facts = CodeFacts(connection);
        Assert.DoesNotContain(facts, fact => fact.SubjectPath.EndsWith("#Gear", StringComparison.Ordinal));
        Assert.Contains(facts, fact => fact.SubjectPath.EndsWith("#Sprocket", StringComparison.Ordinal));

        var gearPath = facts.First(fact => fact.SubjectPath.EndsWith("#Widget", StringComparison.Ordinal))
            .SubjectPath.Replace("#Widget", "#Gear");
        var history = FactStore.History(connection, gearPath, "declared-as");
        var closed = Assert.Single(history);
        Assert.NotNull(closed.ValidTo);
    }

    [Fact]
    public void DeletedFile_HasItsSubjectsClosed_AndItsStateRowRemoved()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        File.Delete(Path.Combine(repo, "Program.cs"));

        var report = Index(connection, sandbox, repo, apply: true);

        Assert.Equal(1, report.Deleted);
        Assert.DoesNotContain(CodeFacts(connection), fact =>
            fact.SubjectPath.Contains("/Program.cs", StringComparison.Ordinal));
        Assert.Equal(0L, ScalarCount(connection,
            "SELECT COUNT(*) FROM file_state WHERE path = 'Program.cs';"));
    }

    /// <summary>
    /// The reason bounding the walk needed a second change. Absence drives deletion, so a scan
    /// that stops early looks exactly like a repository whose files were all removed — the bound
    /// on its own would have converted a slow scan into a destructive one.
    /// </summary>
    [Fact]
    public void PartialScan_DeletesNothing_ThoughEveryIndexedFileIsMissingFromIt()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var before = CodeFacts(connection).Count;
        Assert.True(before > 0, "the fixture has to be indexed for this to prove anything");

        var report = CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(
                repo,
                Apply: true,
                Drain: false,
                Full: true,
                Budget: new ScanBudget(TimeSpan.Zero, 1_000)),
            DateTimeOffset.UtcNow);

        Assert.Equal(0, report.Deleted);
        Assert.Equal(before, CodeFacts(connection).Count);
        Assert.Equal(1L, ScalarCount(connection,
            "SELECT COUNT(*) FROM file_state WHERE path = 'Program.cs';"));
        Assert.Contains(report.Notes, note => note.Contains("partial", StringComparison.Ordinal));
    }

    /// <summary>
    /// commit E2, layer 1: a moved, unmounted, or deleted checkout falls through to Walk the same
    /// way a non-git directory does (GitFileLister.List returns null for an absent root), and its
    /// very first enumeration throws. Before the fix, Walk's catch swallowed that failure and
    /// reported a Complete scan with zero files — licensing D53's guard to close every fact as
    /// deleted and stamp the repo freshly scanned. Falsify by reverting the catch's write to
    /// `stop` in RepoScanner.Walk.
    /// </summary>
    [Fact]
    public void DeletedRoot_AppliesNoDeletions_AndDoesNotStampFullScan()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var identity = CodeIndexer.ResolveIdentity(CodeIndexer.ResolveRoot(repo));
        RepoEnrollment.Enroll(connection, identity, repo, DateTimeOffset.UtcNow);

        var before = CodeFacts(connection).Count;
        Assert.True(before > 0, "the fixture has to be indexed for this to prove anything");

        Directory.Delete(repo, recursive: true);

        var report = CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        Assert.Equal(0, report.Deleted);
        Assert.Equal(before, CodeFacts(connection).Count);
        Assert.Null(RepoEnrollment.Get(connection, identity)!.LastFullScanAt);

        // Layer 2's guard (onDisk empty against prior state) would also produce the assertions
        // above on its own, since a deleted root scans back with zero files either way — these
        // pin the note to the Truncated branch specifically, so reverting only the Walk catch
        // fix (leaving Layer 2 intact) still reddens this test rather than passing on Layer 2's
        // unrelated coverage of the same outward effect.
        Assert.Contains(report.Notes, note => note.Contains("partial", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Notes, note => note.Contains("already indexed", StringComparison.Ordinal));
    }

    /// <summary>
    /// commit E2, layer 2 (§13.5): D53's guard only arms when Truncated is true, but an unmounted
    /// volume's mountpoint is an existing, empty, cleanly enumerable root — Truncated stays false
    /// and D53 does nothing. Falsify by removing the `else if` in CodeIndexer.
    /// </summary>
    [Fact]
    public void EmptyScanAgainstPriorState_SkipsDeletions_AndDoesNotStampFullScan()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var identity = CodeIndexer.ResolveIdentity(CodeIndexer.ResolveRoot(repo));
        RepoEnrollment.Enroll(connection, identity, repo, DateTimeOffset.UtcNow);

        var before = CodeFacts(connection).Count;
        Assert.True(before > 0, "the fixture has to be indexed for this to prove anything");

        File.Delete(Path.Combine(repo, "Program.cs"));
        File.Delete(Path.Combine(repo, "README.md"));

        var report = CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        Assert.Equal(0, report.Deleted);
        Assert.Equal(before, CodeFacts(connection).Count);
        Assert.Contains(report.Notes, note => note.Contains("0 files", StringComparison.Ordinal));
        Assert.Null(RepoEnrollment.Get(connection, identity)!.LastFullScanAt);
    }

    /// <summary>
    /// commit E2, layer 2's negative case: a brand-new repo with nothing indexed yet must not
    /// warn on its first, entirely ordinary empty scan. Falsify by dropping the `states.Count > 0`
    /// clause from the guard.
    /// </summary>
    [Fact]
    public void EmptyRoot_WithNothingPreviouslyIndexed_ProducesNoNote()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "empty-repo");
        Directory.CreateDirectory(repo);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var report = CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: true),
            DateTimeOffset.UtcNow);

        Assert.Equal(0, report.Deleted);
        Assert.Empty(CodeFacts(connection));
        Assert.DoesNotContain(report.Notes, note => note.Contains("already indexed", StringComparison.Ordinal));
    }

    [Fact]
    public void RenamedFile_KeepsItsEntityIds_AndFilesTheOldPathAsAnAlias()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var before = CodeFacts(connection).Single(fact =>
            fact.SubjectPath.EndsWith("#Widget", StringComparison.Ordinal));

        File.Move(Path.Combine(repo, "Program.cs"), Path.Combine(repo, "Machine.cs"));

        var report = Index(connection, sandbox, repo, apply: true);

        Assert.Equal(1, report.Renamed);
        var after = CodeFacts(connection).Single(fact =>
            fact.SubjectPath.EndsWith("#Widget", StringComparison.Ordinal));

        Assert.Equal(before.SubjectId, after.SubjectId);
        Assert.Contains("/Machine.cs#", after.SubjectPath);
        Assert.True(ScalarCount(connection,
            "SELECT COUNT(*) FROM entity_alias WHERE kind = 'path' AND alias LIKE '%Program.cs';") > 0);
        Assert.Equal(0, report.FactsWritten + report.FactsClosed);
    }

    [Fact]
    public void Drain_IndexesOnlyWhatTheQueueNames_AndLeavesOtherReposEntriesQueued()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        File.AppendAllText(Path.Combine(repo, "README.md"), "\nMore prose about usage.\n");
        Spool(sandbox, Path.Combine(repo, "README.md"));
        var foreign = Spool(sandbox, "/somewhere/else/entirely.cs");

        var report = Index(connection, sandbox, repo, apply: true, drain: true);

        Assert.False(report.FullScan);
        Assert.Equal(1, report.Analyzed);
        Assert.Equal(1, report.QueueConsumed);
        Assert.True(File.Exists(foreign), "another repo's entry was consumed by a run that could not act on it");
    }

    [Fact]
    public void Drain_MatchesAnEntrySpooledThroughASymlinkedSpellingOfTheRepo()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        // macOS spells temp two ways (/tmp vs /private/tmp) and a hook records whichever
        // the tool used. Found on the published binary: the entry drained as "another
        // repo's" and leaked in the queue forever.
        var link = Path.Combine(sandbox.Home.Root, "via-link");
        try
        {
            Directory.CreateSymbolicLink(link, repo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        File.AppendAllText(Path.Combine(repo, "README.md"), "\nEdited through the truth.\n");
        Spool(sandbox, Path.Combine(link, "README.md"));

        var report = Index(connection, sandbox, repo, apply: true, drain: true);

        Assert.Equal(1, report.Analyzed);
        Assert.Equal(1, report.QueueConsumed);
        Assert.Empty(Directory.GetFiles(sandbox.Home.QueueDir));
    }

    [Fact]
    public void PathlessQueueEntry_EscalatesADrainToAFullScan()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        // The edit happened, but nothing spooled its path — only a bare timestamp.
        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            ProgramCs.Replace("Widget", "Renamed"));
        Spool(sandbox, path: null);

        var report = Index(connection, sandbox, repo, apply: true, drain: true);

        Assert.True(report.FullScan, "a queue that cannot say what changed must rescan");
        Assert.Contains(CodeFacts(connection), fact =>
            fact.SubjectPath.EndsWith("#Renamed", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(sandbox.Home.QueueDir));
    }

    [Fact]
    public void DryRun_ComputesThePlan_AndWritesNothingAtAll()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var entry = Spool(sandbox, Path.Combine(repo, "Program.cs"));
        var report = Index(connection, sandbox, repo, apply: false, drain: false);

        Assert.True(report.FactsWritten > 0, "the dry run must still say what it would do");
        Assert.Empty(CodeFacts(connection));
        Assert.Equal(0L, ScalarCount(connection, "SELECT COUNT(*) FROM file_state;"));
        Assert.Equal(0L, ScalarCount(connection, "SELECT COUNT(*) FROM repo_registry;"));
        Assert.True(File.Exists(entry), "a dry run consumed a queue entry");
    }

    [Fact]
    public void VersionBump_ForcesEveryFileToBeReRead()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        Execute(connection,
            $"UPDATE schema_meta SET value = '0.0' WHERE key = '{CodeIndexer.VersionKey}';");

        var report = Index(connection, sandbox, repo, apply: true);

        Assert.True(report.VersionForcedFull);
        Assert.Equal(2, report.Analyzed);
        Assert.Equal(0, report.FactsWritten);
    }

    [Fact]
    public void SomeoneElsesFactAtACodePath_IsNeverSupersededOrClosed()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var filePath = CodeFacts(connection)
            .First(fact => fact.SubjectPath.EndsWith("/Program.cs", StringComparison.Ordinal)
                && fact.Predicate == "about")
            .SubjectPath;

        // An agent read the file and recorded its own gist: same subject, same predicate,
        // not regenerable. This supersedes the indexer's impression — that direction is
        // allowed. The indexer taking it back is not (D19).
        FactStore.Remember(
            connection,
            new FactWrite(filePath, "file", "about", "the load-bearing fixture, per the agent",
                "code", "inferred"),
            DateTimeOffset.UtcNow);

        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            ProgramCs.Replace("Turns cranks into torque.", "Entirely new lead comment."));

        var report = Index(connection, sandbox, repo, apply: true);

        Assert.True(report.ProtectedSkipped > 0);
        var live = FactStore.ReadLive(connection).Single(fact =>
            fact.SubjectPath == filePath && fact.Predicate == "about");
        Assert.Equal("the load-bearing fixture, per the agent", live.Body);
        Assert.False(live.Regenerable);
    }

    // gap b (Architect ruling): CoverageOf's three states, each pinned against the real
    // indexer rather than a hand-built file_state row, since the version stamp and the
    // repo/relative-path split are exactly what a real Index() run produces.
    [Fact]
    public void CoverageOf_ATier0File_IsNotApplicable()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var readmePath = CodeFacts(connection)
            .Select(f => f.SubjectPath.Split('#')[0])
            .First(p => p.EndsWith("/README.md", StringComparison.Ordinal));

        Assert.Equal(ExtractionCoverage.NotApplicable, CodeIndexer.CoverageOf(connection, readmePath));
    }

    [Fact]
    public void CoverageOf_ATier2FileIndexedUnderTheCurrentVersion_IsKnownZero()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var programPath = CodeFacts(connection)
            .Select(f => f.SubjectPath.Split('#')[0])
            .First(p => p.EndsWith("/Program.cs", StringComparison.Ordinal));

        Assert.Equal(ExtractionCoverage.KnownZero, CodeIndexer.CoverageOf(connection, programPath));
    }

    [Fact]
    public void CoverageOf_AFileNeverIndexed_IsUnknown()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var programPath = CodeFacts(connection)
            .Select(f => f.SubjectPath.Split('#')[0])
            .First(p => p.EndsWith("/Program.cs", StringComparison.Ordinal));
        var neverIndexed = programPath.Replace("Program.cs", "NeverIndexed.cs", StringComparison.Ordinal);

        Assert.Equal(ExtractionCoverage.Unknown, CodeIndexer.CoverageOf(connection, neverIndexed));
    }

    [Fact]
    public void CoverageOf_AStaleVersionStamp_IsUnknown_EvenForAPreviouslyIndexedFile()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        var programPath = CodeFacts(connection)
            .Select(f => f.SubjectPath.Split('#')[0])
            .First(p => p.EndsWith("/Program.cs", StringComparison.Ordinal));

        Execute(connection, $"UPDATE schema_meta SET value = '0.0' WHERE key = '{CodeIndexer.VersionKey}';");

        Assert.Equal(ExtractionCoverage.Unknown, CodeIndexer.CoverageOf(connection, programPath));
    }

    [Fact]
    public void UnstagedEdit_InARealCheckout_IsStillDetected()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox);
        if (!Git(repo, "init", "-q")
            || !Git(repo, "add", "-A")
            || !Git(repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "fixture"))
        {
            return;
        }

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo, apply: true);

        // Edited and NOT staged — the state every file is in the moment the hook fires.
        // ls-files -s still reports the committed blob for it; found on the published
        // binary as "0 analyzed, 1 unchanged" right after an edit.
        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            ProgramCs.Replace("public enum Gear { Low }", "public enum Sprocket { Fine }"));

        var report = Index(connection, sandbox, repo, apply: true);

        Assert.Equal(1, report.Analyzed);
        Assert.Contains(CodeFacts(connection), fact =>
            fact.SubjectPath.EndsWith("#Sprocket", StringComparison.Ordinal));
    }

    private static bool Git(string directory, params string[] args)
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
            foreach (var argument in args)
            {
                info.ArgumentList.Add(argument);
            }

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

    private static string CreateFixture(SandboxHome sandbox)
    {
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Program.cs"), ProgramCs);
        File.WriteAllText(Path.Combine(repo, "README.md"), ReadmeMd);
        return repo;
    }

    private static IndexReport Index(
        SqliteConnection connection,
        SandboxHome sandbox,
        string repo,
        bool apply,
        bool drain = false)
        => CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, apply, drain, Full: false),
            DateTimeOffset.UtcNow);

    private static List<StoredFact> CodeFacts(SqliteConnection connection) =>
        FactStore.ReadLive(connection)
            .Where(fact => fact.SubjectPath.StartsWith("/projects/", StringComparison.Ordinal)
                && fact.SubjectPath.Contains("/code/", StringComparison.Ordinal))
            .ToList();

    private static string Spool(SandboxHome sandbox, string? path)
    {
        Directory.CreateDirectory(sandbox.Home.QueueDir);
        var file = Path.Combine(
            sandbox.Home.QueueDir,
            $"{DateTime.UtcNow.Ticks}-{Guid.NewGuid():N}.spool");
        var body = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + (path is null ? "\n" : $"\n{path}\n");
        File.WriteAllText(file, body);
        return file;
    }

    private static long ScalarCount(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
