using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Acceptance tests for code-navigation Phase 3 (docs/code-navigation-phase3-spec.md §10).
/// The query layer (callers/callees over the fact store) is tested unconditionally by seeding
/// facts directly, the same way CodeNavigationPhase2Tests proves fact-diff behavior without a
/// live extractor. Extraction itself needs a real tree-sitter grammar, which — like
/// TreeSitterTests.cs — is only present when ENGRAM_TEST_TREE_SITTER_DIR points at one;
/// those tests are gated the same way and skip cleanly without it.
/// </summary>
public sealed class CodeNavigationPhase3Tests
{
    private static readonly McpSessionId Session = new("test-session");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    // §6.5: LeafOf, two separators, the spec's 8-case table.
    [Theory]
    [InlineData("join", "join")]
    [InlineData("path.join", "join")]
    [InlineData("os.path.join", "join")]
    [InlineData("Outer/Inner", "Inner")]
    [InlineData("Outer/Inner(T, U)", "Inner(T, U)")]
    [InlineData("Outer/Inner(System.String s)", "Inner(System.String s)")]
    [InlineData("this.Foo", "Foo")]
    [InlineData("trailing/", "")]
    [InlineData("", "")]
    public void LeafOf_MatchesTheSpecTable(string name, string expectedLeaf)
    {
        Assert.Equal(expectedLeaf, CodePaths.LeafOf(name));
    }

    // C4 / §6.1: a leaf-name search over `calls` objects finds every distinct written spelling
    // sharing that leaf, whether written bare or qualified.
    [Fact]
    public void Callers_LeafNameJoin_FindsCallsWrittenWithAQualifier()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#outer", "outer");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#other", "other");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#helper", "helper");
        SeedCall(connection, "/projects/p/code/r/a.ts#outer", "helper");
        SeedCall(connection, "/projects/p/code/r/a.ts#other", "ns.helper");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "helper", "callers");

        Assert.Contains("outer", result);
        Assert.Contains("other", result);
    }

    // §6.1(iii): an ambiguous name's callers response states the declaration count.
    [Fact]
    public void Callers_AmbiguousDeclaration_LabelsTheAmbiguity()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/a/a.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/b/b.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/a/a.ts#caller", "caller");
        SeedCall(connection, "/projects/p/code/a/a.ts#caller", "run");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "run", "callers");

        Assert.Contains("2 declarations", result);
    }

    // item 27 (§6.1(iii), N2): the ambiguity count is scoped to the repo the caller asked for,
    // not the whole store — 2 declarations of "run" in repoa, a third in repob that must not
    // inflate the count.
    [Fact]
    public void Callers_AmbiguousDeclaration_ScopesTheCountToTheRequestedRepo()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/repoa/a.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/repoa/b.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/repob/c.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/repoa/a.ts#caller", "caller");
        SeedCall(connection, "/projects/p/code/repoa/a.ts#caller", "run");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "run", "callers", repo: "repoa");

        Assert.Contains("2 declarations", result);
    }

    // item 29 (new): a repo-scoped callers/callees query returns only what the target repo has,
    // and excludes matches from other repos sharing the same leaf name — the defect
    // MatchingSymbolNames's dropped repo filter (§6.1(iii)/item 27 investigation) was masking:
    // before the fix every repo-scoped call returned nothing at all, which happened to also
    // "exclude" other repos, for the wrong reason.
    [Fact]
    public void Callers_RepoScoped_ExcludesCallSitesFromOtherRepos()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/repoa/a.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/repob/b.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/repoa/a.ts#callerA", "callerA");
        SeedSymbol(connection, "/projects/p/code/repob/b.ts#callerB", "callerB");
        SeedCall(connection, "/projects/p/code/repoa/a.ts#callerA", "run");
        SeedCall(connection, "/projects/p/code/repob/b.ts#callerB", "run");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "run", "callers", repo: "repoa");

        Assert.Contains("callerA", result);
        Assert.DoesNotContain("callerB", result);
    }

    [Fact]
    public void Callees_RepoScoped_ResolvesOnlyToDeclarationsInTheTargetRepo()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/repoa/a.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/repob/b.ts#run", "run");
        SeedSymbol(connection, "/projects/p/code/repoa/a.ts#outer", "outer");
        SeedCall(connection, "/projects/p/code/repoa/a.ts#outer", "run");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "outer", "callees", repo: "repoa");

        Assert.Contains("/projects/p/code/repoa/a.ts#run", result);
        Assert.DoesNotContain("/projects/p/code/repob/b.ts#run", result);
    }

    // item 28, half 1 (§6.3.1): when exact and case-insensitive both miss and substring
    // answers, the label must say so rather than silently folding the substring matches in.
    [Fact]
    public void Callers_ResolvedBySubstring_ReportsTheMatchTier()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Running", "Running");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#caller", "caller");
        SeedCall(connection, "/projects/p/code/r/a.ts#caller", "Running");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "unn", "callers");

        Assert.Contains("substring", result, StringComparison.OrdinalIgnoreCase);
    }

    // item 28, half 2 (§6.3.1): a qualifier that always fires is noise — an exact match must
    // say nothing about tiers at all.
    [Fact]
    public void Callers_ResolvedExactly_SaysNothingAboutTiers()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#outer", "outer");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#helper", "helper");
        SeedCall(connection, "/projects/p/code/r/a.ts#outer", "helper");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "helper", "callers");

        Assert.DoesNotContain("substring", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("case-insensitive", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No exact match", result, StringComparison.OrdinalIgnoreCase);
    }

    // item 28, both halves, callees direction (§6.3.1 names :97 alongside :67).
    [Fact]
    public void Callees_ResolvedBySubstring_ReportsTheMatchTier()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Running", "Running");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#helper", "helper");
        SeedCall(connection, "/projects/p/code/r/a.ts#Running", "helper");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "unn", "callees");

        Assert.Contains("substring", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Callees_ResolvedExactly_SaysNothingAboutTiers()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#outer", "outer");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#helper", "helper");
        SeedCall(connection, "/projects/p/code/r/a.ts#outer", "helper");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "outer", "callees");

        Assert.DoesNotContain("substring", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("case-insensitive", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No exact match", result, StringComparison.OrdinalIgnoreCase);
    }

    // §6.2: callees is the join direction — resolves X's own declaration, then its outgoing
    // `calls` facts, each enriched with the callee's declaration site. Same-file caller/callee
    // ranks [same-file] (§6.4 signal 1).
    [Fact]
    public void Callees_ResolvesOutgoingCallsToTheirDeclarationSites()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#outer", "outer");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#inner", "inner");
        SeedCall(connection, "/projects/p/code/r/a.ts#outer", "inner");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "outer", "callees");

        Assert.Contains("inner", result);
        Assert.Contains("[same-file]", result);
    }

    // B3: the query's own resolution (`:67`/`:97`) reaches the Substring tier, same as
    // defined_at — a half-remembered query name should still find the symbol it names.
    [Theory]
    [InlineData("callers")]
    [InlineData("callees")]
    public void QueryResolution_FallsBackToSubstring_ForCallersAndCallees(string relation)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#outer", "outer");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#ConfigureWidget", "ConfigureWidget");
        SeedCall(connection, "/projects/p/code/r/a.ts#outer", "ConfigureWidget");

        var query = relation == "callers" ? "onfigureWidge" : "oute";
        var result = EngramMcpTools.Navigate(sandbox.Home, Session, query, relation);

        Assert.DoesNotContain("No symbol named", result);
        var expectedPath = relation == "callers" ? "outer" : "ConfigureWidget";
        Assert.Contains(expectedPath, result);
    }

    // §6.3: the per-edge leaf-name join must not resolve a call naming "Foo" to a
    // declaration named "FooBar" — the CaseInsensitive ceiling at `:86` never falls through
    // to Substring. B3 dropped the *query*-resolution ceiling at `:45`/`:73`, which used to
    // make this guard fail for the wrong reason (query resolution itself, never reaching the
    // join); this exercises the ceiling that must remain.
    [Fact]
    public void Callees_LeafJoin_DoesNotFalsePositiveOnASubstringName()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#caller", "caller");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#Foo", "Foo");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#FooBar", "FooBar");
        SeedCall(connection, "/projects/p/code/r/a.ts#caller", "Foo");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "caller", "callees");

        Assert.Contains("-> /projects/p/code/r/a.ts#Foo", result);
        Assert.DoesNotContain("FooBar", result);
    }

    // §10 item 15 / §6.1(iv): callers and callees share one ranker. Falsify by giving
    // `callers` its own copy of `RankFrom` (e.g. hardcode a different signal for it) — this
    // reddens because both directions must report [same-file] for the identical shape.
    [Theory]
    [InlineData("callers")]
    [InlineData("callees")]
    public void SameFileSignal_IsIdenticalAcrossBothDirections(string relation)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#outer", "outer");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#inner", "inner");
        SeedCall(connection, "/projects/p/code/r/a.ts#outer", "inner");

        var query = relation == "callers" ? "inner" : "outer";
        var result = EngramMcpTools.Navigate(sandbox.Home, Session, query, relation);

        Assert.Contains("[same-file]", result);
    }

    // §10 item 17 / §6.4: the reported reason for the filename-heuristic signal names a
    // filename match, not "import-consistency" — a wrong-but-plausible reason string is a
    // false explanation (D30).
    [Fact]
    public void ImportFilenameMatchSignal_IsLabelledAsAFilenameMatch_NotImportConsistency()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/caller.ts#outer", "outer");
        SeedSymbol(connection, "/projects/p/code/r/target.ts#inner", "inner");
        SeedCall(connection, "/projects/p/code/r/caller.ts#outer", "inner");
        SeedImport(connection, "/projects/p/code/r/caller.ts", "target");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "outer", "callees");

        Assert.Contains("[import-filename-match]", result);
        Assert.DoesNotContain("import-consistent", result);
    }

    // gap a: ImportFilenameMatch must outrank SameRepo — both callers below are in the same
    // repo as the target, but only one has a stated import naming the target's file, so its
    // line must sort before the same-repo-only caller's (Architect ruling; CallRankSignal's
    // declared order is what OrderBy(m => m.Signal) reads as priority).
    [Fact]
    public void ImportFilenameMatchSignal_OutranksSameRepoSignal_InCallerOrdering()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/target.ts#target", "target");
        SeedSymbol(connection, "/projects/p/code/r/same_repo_only.ts#a", "a");
        SeedSymbol(connection, "/projects/p/code/r/imports_target.ts#b", "b");
        SeedCall(connection, "/projects/p/code/r/same_repo_only.ts#a", "target");
        SeedCall(connection, "/projects/p/code/r/imports_target.ts#b", "target");
        SeedImport(connection, "/projects/p/code/r/imports_target.ts", "target");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "target", "callers");

        var importRank = result.IndexOf("imports_target.ts", StringComparison.Ordinal);
        var repoRank = result.IndexOf("same_repo_only.ts", StringComparison.Ordinal);
        Assert.True(importRank >= 0 && repoRank >= 0, result);
        Assert.True(importRank < repoRank, "ImportFilenameMatch must sort before SameRepo:\n" + result);
    }

    // §10 item 25 / S3, retargeted by the all-members spec (§3.1): after widening emission to
    // every visibility, a private method's calls attribute to the method itself, not the
    // type — so this guard no longer tests that case. What still folds to the enclosing type
    // is a call inside a kind neither tier emits as a symbol (indexer, operator, enum-member
    // initializer); an indexer body is the clearest of those. The rendered output still says
    // so — never silently, per the Architect's ruling.
    [Fact]
    public void CallerInAnIndexerBody_AttributesToTheEnclosingType_CarriesTheAttributionLabel()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedType(connection, "/projects/p/code/r/a.cs#Caller", "Caller");
        SeedSymbol(connection, "/projects/p/code/r/a.cs#target", "target");
        SeedCall(connection, "/projects/p/code/r/a.cs#Caller", "target");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "target", "callers");

        Assert.Contains("attributed to the enclosing type", result);
    }

    // Contrast arm: a call in a public method attributes to the method itself and carries
    // no attribution label — the label must fire only for the coarse case.
    [Fact]
    public void CallerAttributedToAMethod_CarriesNoAttributionLabel()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.cs#Caller/Run()", "Run");
        SeedSymbol(connection, "/projects/p/code/r/a.cs#target", "target");
        SeedCall(connection, "/projects/p/code/r/a.cs#Caller/Run()", "target");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "target", "callers");

        Assert.DoesNotContain("attributed to the enclosing type", result);
    }

    // §8.4's corollary: an advertised-but-unbuilt relation is worse than an unadvertised one
    // under a first-reach mandate, so `neighbors` is retired rather than kept refusing.
    [Fact]
    public void Neighbors_IsNoLongerASpecialCase_AndIsRejectedLikeAnyUnknownRelation()
    {
        using var sandbox = new SandboxHome();

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "anything", "neighbors");

        Assert.Contains("Unknown relation 'neighbors'", result);
    }

    [Theory]
    [InlineData("callers")]
    [InlineData("callees")]
    public void CallersAndCallees_NoLongerAnswerNotYetIndexed_WhenTheSymbolExists(string relation)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#outer", "outer");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#inner", "inner");
        SeedCall(connection, "/projects/p/code/r/a.ts#outer", "inner");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "outer", relation);

        Assert.DoesNotContain("not yet indexed", result);
    }

    // Real tier-1 extraction (D47), gated the same way TreeSitterTests.cs gates every other
    // tree-sitter-driven test: skips cleanly without ENGRAM_TEST_TREE_SITTER_DIR, runs for real
    // wherever fetch-tree-sitter.sh has populated it.
    [Fact]
    public void IndexingATypeScriptFile_YieldsLiveCallsFacts()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "ts-repo", "widget.ts",
            "function outer() {\n  inner();\n}\n\nfunction inner() {\n  helper();\n}\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var live = FactStore.ReadLive(connection).Where(f => f.Predicate == "calls").ToList();
        Assert.NotEmpty(live);
    }

    // code-navigation Phase 4 spec §9 item 7 (tree-sitter half): a tree-sitter-language
    // `calls` fact reads analyzer_tier = 1 in the database.
    [Fact]
    public void IndexingATypeScriptFile_StampsAnalyzerTierOne_OnCallsFacts()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "ts-repo", "widget.ts",
            "function outer() {\n  inner();\n}\n\nfunction inner() {\n  helper();\n}\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var live = FactStore.ReadLive(connection).Where(f => f.Predicate == "calls").ToList();
        Assert.NotEmpty(live);
        Assert.All(live, f => Assert.Equal(1, f.AnalyzerTier));
    }

    // §8: CodeIndexer's generic (EntityPath, Predicate, Object) diff key already distinguishes
    // two distinct callees from the same caller — re-indexing must not close either edge.
    [Fact]
    public void ReindexingUnchangedFile_KeepsBothDistinctCallsEdgesFromOneCaller_Live()
    {
        var dir = GrammarDir();
        Assert.SkipWhen(dir is null, "ENGRAM_TEST_TREE_SITTER_DIR does not point at the compiled grammars.");

        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "ts-repo", "widget.ts",
            "function outer() {\n  a();\n  b();\n}\n\nfunction a() {}\nfunction b() {}\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);
        Index(connection, sandbox, repo);

        var live = FactStore.ReadLive(connection)
            .Where(f => f.Predicate == "calls" && f.SubjectPath.EndsWith("#outer", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, live.Count);
    }

    private static string? GrammarDir()
    {
        var dir = Environment.GetEnvironmentVariable("ENGRAM_TEST_TREE_SITTER_DIR");
        return dir is { Length: > 0 } && File.Exists(Path.Combine(dir, TreeSitter.CoreLibraryFile))
            ? dir
            : null;
    }

    private static void SeedSymbol(SqliteConnection connection, string path, string name)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        FactStore.EnsureEntity(connection, transaction, path, "symbol", T0.ToUnixTimeSeconds(), name);
        transaction.Commit();
    }

    private static void SeedType(SqliteConnection connection, string path, string name) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "symbol", "declared-as", "public class " + name, "code", "observed", Regenerable: true),
            T0);

    private static void SeedImport(SqliteConnection connection, string filePath, string moduleName) =>
        FactStore.Remember(
            connection,
            new FactWrite(filePath, "file", "imports", "imports " + moduleName, "code", "observed",
                Regenerable: true, ObjectPath: CodePaths.ForSymbolName(moduleName), ObjectKind: "symbol-name"),
            T0);

    private static void SeedCall(SqliteConnection connection, string callerPath, string callee) =>
        FactStore.Remember(
            connection,
            new FactWrite(callerPath, "symbol", "calls", "calls " + callee, "code", "observed",
                Regenerable: true, ObjectPath: CodePaths.ForSymbolName(callee), ObjectKind: "symbol-name"),
            T0);

    private static string CreateFixture(SandboxHome sandbox, string repoDirName, string fileName, string contents)
    {
        var repo = Path.Combine(sandbox.Home.Root, repoDirName);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, fileName), contents);
        return repo;
    }

    private static void Index(SqliteConnection connection, SandboxHome sandbox, string repo) =>
        CodeIndexer.Index(
            connection,
            sandbox.Home,
            ConfigFile.Empty,
            IndexingSettings.Default,
            new IndexOptions(repo, Apply: true, Drain: false, Full: false),
            DateTimeOffset.UtcNow);
}
