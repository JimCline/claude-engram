using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>Acceptance tests for code-navigation Phase 2 (docs/code-navigation-phase2-spec.md §10).</summary>
public sealed class CodeNavigationPhase2Tests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    // §10 item 9: SymbolNameOf(ForSymbolName(n)) == n, including the adversarial cases §5.2 names.
    [Theory]
    [InlineData("./a/b")]
    [InlineData("@scope/pkg")]
    [InlineData("a%b")]
    [InlineData("Foo.Bar")]
    [InlineData("os.path.join")]
    [InlineData("a%2Fb")]
    public void SymbolNameOf_InvertsForSymbolName(string name)
    {
        var path = CodePaths.ForSymbolName(name);
        Assert.Equal(name, CodePaths.SymbolNameOf(path));
    }

    [Fact]
    public void ForSymbolName_IsNotUnderTheLocationAddressingRoot()
    {
        var path = CodePaths.ForSymbolName("react");
        Assert.StartsWith("/symbol-names/", path, StringComparison.Ordinal);
        Assert.Null(CodePaths.SymbolNameOf("/some/other/path"));
    }

    // §10 item 2: two distinct edges from one subject on one predicate both stay live.
    [Fact]
    public void TwoDistinctEdges_FromOneSubjectOnOnePredicate_BothStayLive()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        FactStore.Remember(
            connection,
            new FactWrite("/projects/x/code/a.cs", "file", "imports", "imports react", "code", "observed",
                Regenerable: true, ObjectPath: CodePaths.ForSymbolName("react"), ObjectKind: "symbol-name"),
            T0);
        FactStore.Remember(
            connection,
            new FactWrite("/projects/x/code/a.cs", "file", "imports", "imports lodash", "code", "observed",
                Regenerable: true, ObjectPath: CodePaths.ForSymbolName("lodash"), ObjectKind: "symbol-name"),
            T0);

        var live = FactStore.ReadLive(connection)
            .Where(f => f.SubjectPath == "/projects/x/code/a.cs" && f.Predicate == "imports")
            .ToList();
        Assert.Equal(2, live.Count);
    }

    // §10 item 3: an ordinary (objectless) predicate still closes and supersedes its predecessor.
    [Fact]
    public void OrdinaryFact_StillClosesAndSupersedesItsPredecessor()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var first = FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/kestrel", "note", "states", "It binds loopback only.", "project", "stated"),
            T0).FactId;
        var second = FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/kestrel", "note", "states", "It binds 0.0.0.0 now.", "project", "stated"),
            T0.AddMinutes(1)).FactId;

        var live = FactStore.ReadLive(connection)
            .Single(f => f.SubjectPath == "/knowledge/testing/kestrel" && f.Predicate == "states");
        Assert.Equal(second, live.Id);

        var closed = FactStore.ReadById(connection, first);
        Assert.NotNull(closed);
        Assert.NotNull(closed.ValidTo);
        Assert.Equal(second, closed.SupersededBy);
    }

    // §10 item 4: indexing a file with three imports yields three live `imports` facts with three
    // distinct, correctly-named symbol-name objects.
    [Fact]
    public void IndexingThreeImports_YieldsThreeLiveEdgesWithCorrectlyNamedObjects()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            "using System.Text;\nusing System.Linq;\nusing System.IO;\n\npublic sealed class Widget { }");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RunIndex(connection, sandbox, repo);

        var live = FactStore.ReadLive(connection)
            .Where(f => f.SubjectPath.EndsWith("/Program.cs", StringComparison.Ordinal) && f.Predicate == "imports")
            .ToList();
        Assert.Equal(3, live.Count);

        var objectNames = ObjectNamesFor(connection, live.Select(f => f.Id));
        Assert.Equal(
            new HashSet<string> { "System.Text", "System.Linq", "System.IO" },
            objectNames);
    }

    // §10 item 5: re-indexing a store written under AnalyzerVersion 2 (single joined-string
    // objectless import fact) leaves zero live `imports` facts with object_id IS NULL.
    [Fact]
    public void ReindexingAnAnalyzerVersion2Store_LeavesNoObjectlessLiveImportsFacts()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        var filePath = Path.Combine(repo, "Program.cs");
        File.WriteAllText(filePath, "using System.Text;\nusing System.Linq;\n\npublic sealed class Widget { }");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RunIndex(connection, sandbox, repo);

        var subjectPath = FactStore.ReadLive(connection)
            .First(f => f.SubjectPath.EndsWith("/Program.cs", StringComparison.Ordinal) && f.Predicate == "imports")
            .SubjectPath;

        // Simulate a leftover an AnalyzerVersion-2 run left behind at the same subject: one
        // joined-string, objectless `imports` fact, regenerable, coexisting beside the edges the
        // real run above already wrote (a different predicate-object pair, so it does not collide).
        FactStore.Remember(
            connection,
            new FactWrite(subjectPath, "file", "imports", "imports System.Text, System.Linq", "code", "observed",
                Regenerable: true),
            T0);

        // Roll the stored indexer version back to what AnalyzerVersion 2 actually wrote, so a
        // plain re-index (full: false, unchanged file SHA) is forced to re-read this file by the
        // real versionForcedFull path (CodeIndexer.cs:127-130), not by bypassing it with
        // full: true — passing full: true directly would re-analyze every file regardless of
        // version, leaving the real version-forced path unexercised.
        EngramDatabase.WriteMeta(connection, null, CodeIndexer.VersionKey, $"{CodePaths.GrammarVersion}.2");

        RunIndex(connection, sandbox, repo, full: false);

        var liveImports = FactStore.ReadLive(connection)
            .Where(f => f.SubjectPath == subjectPath && f.Predicate == "imports")
            .ToList();
        Assert.NotEmpty(liveImports);
        Assert.DoesNotContain(liveImports, f => ObjectIdFor(connection, f.Id) is null);
    }

    // §10 item 6: fact_fts and fact_token row counts are unchanged by an index run that writes
    // edges, verified through fts5vocab rather than a bare `SELECT rowid FROM fact_fts` (CLAUDE.md:
    // on an external-content table the latter is answered from the content table and cannot see a
    // real desync).
    [Fact]
    public void IndexingEdges_LeavesFactFtsAndFactTokenUntouched()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            "/// <summary>Turns cranks into torque.</summary>\nusing System.Text;\nusing System.Linq;\n\npublic sealed class Widget { }");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RunIndex(connection, sandbox, repo);

        var edgeFactIds = FactStore.ReadLive(connection)
            .Where(f => f.Predicate == "imports")
            .Select(f => f.Id)
            .ToList();
        Assert.NotEmpty(edgeFactIds);

        // A bare non-MATCH query against fact_fts (SELECT ... WHERE rowid = $id) is answered from
        // the external-content table (fact) and would report every existing fact as "indexed"
        // regardless of whether the trigger actually inserted it — fts5vocab is how the real
        // index is read (CLAUDE.md).
        using var vocab = connection.CreateCommand();
        vocab.CommandText =
            "CREATE VIRTUAL TABLE IF NOT EXISTS temp.vocab_check USING fts5vocab('main', 'fact_fts', 'instance');";
        vocab.ExecuteNonQuery();

        foreach (var id in edgeFactIds)
        {
            using var inVocab = connection.CreateCommand();
            inVocab.CommandText = "SELECT count(*) FROM temp.vocab_check WHERE doc = $id;";
            inVocab.Parameters.AddWithValue("$id", id);
            Assert.Equal(0L, (long)inVocab.ExecuteScalar()!);

            using var inToken = connection.CreateCommand();
            inToken.CommandText = "SELECT count(*) FROM fact_token WHERE fact_id = $id;";
            inToken.Parameters.AddWithValue("$id", id);
            Assert.Equal(0L, (long)inToken.ExecuteScalar()!);
        }

        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE temp.vocab_check;";
        drop.ExecuteNonQuery();
    }

    // §10 item 7: the recall path's candidate row count over N facts + 5N edges is N, asserted
    // through FactCatalog.ReadLongTerm — the retrieval path itself (§5.5) — rather than through
    // FactStore.ReadLive(excludeEdges: true) directly, which would only prove the parameter works
    // and not that the retrieval path actually passes it.
    [Fact]
    public void ReadLongTerm_ExcludesEdgesFromTheRecallPath()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        const int N = 4;
        for (var i = 0; i < N; i++)
        {
            FactStore.Remember(
                connection,
                new FactWrite($"/projects/x/code/f{i}.cs", "file", "about", $"file {i}", "code", "observed",
                    Regenerable: true),
                T0);

            for (var j = 0; j < 5; j++)
            {
                FactStore.Remember(
                    connection,
                    new FactWrite($"/projects/x/code/f{i}.cs", "file", "imports", $"imports mod{j}", "code", "observed",
                        Regenerable: true, ObjectPath: CodePaths.ForSymbolName($"mod{i}_{j}"), ObjectKind: "symbol-name"),
                    T0);
            }
        }

        // CannedFact.Subject carries the entity's display name (e.g. "f0.cs"), not its path, so
        // this test's own facts are picked out by their distinctive body instead — the seeded
        // knowledge facts never start with "file ". The body prefix scopes the count to this
        // test's subjects but must not also be what removes the edges — the DoesNotContain below
        // runs over the unfiltered result so it actually exercises excludeEdges: true.
        var ours = FactCatalog.ReadLongTerm(connection, T0)
            .Where(f => f.Body.StartsWith("file ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(N, ours.Count);
        Assert.DoesNotContain(FactCatalog.ReadLongTerm(connection, T0), f => f.Predicate == "imports");
    }

    // §10 item 8: §7's static and data guards.
    [Fact]
    public void EveryEdgeBearingCandidate_CarriesANonNullObject_StaticGuard()
    {
        // Asserts over the real emission site (CodeAnalyzer's own candidates) rather than
        // CodePredicates.EdgeBearing's cardinality — a correct Phase 3 addition of "calls" would
        // false-fail an Assert.Single on the set, which is not what this guard is for. Falsify by
        // removing `Object:` from CodeAnalyzer.AddImports.
        var source = "using System.Text;\nusing System.Linq;\n\npublic sealed class Widget { }";
        var language = LanguageRegistry.All.Single(l => l.Id == "csharp");
        var candidates = CodeAnalyzer.Analyze("/projects/x/code/Program.cs", source, language);

        var edgeBearing = candidates.Where(c => CodePredicates.EdgeBearing.Contains(c.Predicate)).ToList();
        Assert.NotEmpty(edgeBearing);
        Assert.All(edgeBearing, c => Assert.NotNull(c.Object));
    }

    [Fact]
    public void NoLivePredicate_IsBothObjectlessAndObjectBearing_DataGuard()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            "using System.Text;\n\npublic sealed class Widget { }");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RunIndex(connection, sandbox, repo);

        var liveFacts = FactStore.ReadLive(connection);
        Assert.Contains(liveFacts, f => !CodePredicates.EdgeBearing.Contains(f.Predicate));
        Assert.Contains(liveFacts, f => CodePredicates.EdgeBearing.Contains(f.Predicate));

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT predicate FROM fact WHERE valid_to IS NULL AND object_id IS     NULL
            INTERSECT
            SELECT predicate FROM fact WHERE valid_to IS NULL AND object_id IS NOT NULL;
            """;
        using var reader = command.ExecuteReader();
        Assert.False(reader.Read());
    }

    // §10 item 10: engram_navigate imports still answers correctly, now over multiple live edges.
    [Fact]
    public void NavigateImports_ReportsEveryLiveModule_NotJustOne()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            "using System.Text;\nusing System.Linq;\n\npublic sealed class Widget { }");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RunIndex(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, new McpSessionId("test-session"), "Program.cs", "imports");

        Assert.Contains("imports System.Linq, System.Text", result);
    }

    // §7.2: every live edge-bearing fact's object entity is addressed under /symbol-names/ after
    // a real index run — the detector for FactWrite.ObjectPath being handed a raw, unconverted
    // name at some call site, the same silent-looks-fine failure mode as N1.
    [Fact]
    public void AfterARealIndexRun_EveryLiveEdgeObject_IsASymbolNamesPath()
    {
        using var sandbox = new SandboxHome();
        var repo = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(
            Path.Combine(repo, "Program.cs"),
            "using System.Text;\nusing System.Linq;\n\npublic sealed class Widget { }");

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        RunIndex(connection, sandbox, repo);

        var edgeFactIds = FactStore.ReadLive(connection)
            .Where(f => CodePredicates.EdgeBearing.Contains(f.Predicate))
            .Select(f => f.Id)
            .ToList();
        Assert.NotEmpty(edgeFactIds);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT e.path FROM fact f JOIN entity e ON e.id = f.object_id
             WHERE f.valid_to IS NULL AND f.predicate IN ({CodePredicates.EdgeBearingSqlList})
               AND e.path NOT LIKE '/symbol-names/%';
            """;
        using var reader = command.ExecuteReader();
        Assert.False(reader.Read());
    }

    private static void RunIndex(SqliteConnection connection, SandboxHome sandbox, string repo, bool full = false) =>
        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: full),
            DateTimeOffset.UtcNow);

    private static long? ObjectIdFor(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT object_id FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() as long?;
    }

    private static HashSet<string> ObjectNamesFor(SqliteConnection connection, IEnumerable<long> factIds)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in factIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT o.path FROM fact f JOIN entity o ON o.id = f.object_id WHERE f.id = $id;";
            command.Parameters.AddWithValue("$id", id);
            var path = (string)command.ExecuteScalar()!;
            names.Add(CodePaths.SymbolNameOf(path)!);
        }

        return names;
    }
}
