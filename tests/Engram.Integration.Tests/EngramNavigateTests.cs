using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

public class EngramNavigateTests
{
    private static readonly McpSessionId Session = new("test-session");

    private const string ProgramCs = """
        using System.Text;

        public sealed class Widget { }

        public sealed class Helper { }
        """;


    [Fact]
    public void DefinedAt_ExactMatch_ReturnsPathAndDeclaration()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.Contains("[Exact]", result);
        Assert.Contains("#Widget", result);
        Assert.Contains("class Widget", result);
    }

    [Fact]
    public void DefinedAt_CaseInsensitiveFallback_IsLabelled()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "widget", "defined_at");

        Assert.Contains("[CaseInsensitive]", result);
    }

    [Fact]
    public void DefinedAt_SubstringFallback_IsLabelled_ThroughTheTool()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "idg", "defined_at");

        Assert.Contains("[Substring]", result);
        Assert.Contains("#Widget", result);
    }

    [Fact]
    public void DefinedAt_NoMatch_SaysSo()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "NoSuchSymbol", "defined_at");

        Assert.Contains("No symbol named", result);
    }

    [Fact]
    public void Imports_ReturnsTheFilesImportFact()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Program.cs", "imports");

        Assert.Contains("System.Text", result);
    }

    [Fact]
    public void UnknownRelation_IsRejected()
    {
        using var sandbox = new SandboxHome();

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "bogus");

        Assert.Contains("Unknown relation", result);
    }

    [Fact]
    public void Response_CarriesTheExtractionTierHeader_ForARegexOnlyIndex()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.Contains("(extraction tier: regex)", result);
    }

    // Prefix-sharing slugs: "/code/engram/" must not match "/code/engram-docs/" (fixup B2,
    // architect constraint 1). Two repos indexed side by side, query scoped to the shorter slug.
    [Fact]
    public void Repo_BracketingSlashes_DoNotMatchAPrefixSharingSlug()
    {
        using var sandbox = new SandboxHome();
        var engram = CreateFixture(sandbox, "engram", "Widget.cs", "public sealed class OnlyInEngram { }");
        var engramDocs = CreateFixture(sandbox, "engram-docs", "Other.cs", "public sealed class OnlyInEngramDocs { }");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, engram);
        Index(connection, sandbox, engramDocs);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "OnlyInEngramDocs", "defined_at", repo: "engram");

        Assert.Contains("No symbol named", result);
    }

    // Constraint 2: the slug normalization used to filter must agree with the one the indexer
    // used to write the path, or the filter silently matches nothing.
    [Fact]
    public void Repo_NormalizationAgreesWithTheIndexers()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "Fixture Repo", "Widget.cs", "public sealed class Widget { }");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at", repo: "Fixture Repo");

        Assert.Contains("[Exact]", result);
    }

    // The false-negative fixup B2 fixes: an exact-tier match sitting in a different repo used
    // to block the fallback that would have found the in-repo substring match.
    [Fact]
    public void Repo_ExactMatchElsewhere_DoesNotBlockAnInRepoSubstringMatch()
    {
        using var sandbox = new SandboxHome();
        var other = CreateFixture(sandbox, "other-repo", "Widget.cs", "public sealed class Widget { }");
        var target = CreateFixture(sandbox, "target-repo", "Factory.cs", "public sealed class WidgetFactory { }");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, other);
        Index(connection, sandbox, target);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at", repo: "target-repo");

        Assert.Contains("[Substring]", result);
        Assert.Contains("WidgetFactory", result);
    }

    [Fact]
    public void Repo_Unknown_AnswersNotIndexed_NeverEmptyOrGenericNotFound()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at", repo: "no-such-repo");

        Assert.Contains("is not indexed", result);
        Assert.DoesNotContain("No symbol named", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1_000_000)]
    public void Limit_IsClampedToOneHundred(int requested)
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        // Clamping is proved by absence of a crash/timeout on an unbounded request and by a
        // still-correct answer; SQLite would otherwise treat a non-positive LIMIT as unbounded.
        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at", limit: requested);

        Assert.Contains("[Exact]", result);
    }

    [Fact]
    public void DefinedAt_NoDeclarationRecorded_IsReported()
    {
        using var sandbox = new SandboxHome();
        var repoPath = Path.Combine(sandbox.Home.Root, "fixture-repo");
        Directory.CreateDirectory(repoPath);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var entityPath = "/projects/fixture-repo/code/fixture-repo/Widget.cs#Widget";
        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            FactStore.EnsureEntity(
                connection, transaction, entityPath, "symbol", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Widget");
            transaction.Commit();
        }

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.Contains("(no declaration recorded)", result);
    }

    // R1: navigate telemetry is D71's adoption evidence for the M3 override, and nothing
    // asserted it was actually written. Covers both a hit and a not-yet-indexed relation, since
    // B1's fix runs the same Telemetry.Append call for every branch of Navigate.
    [Fact]
    public void Navigate_EmitsNavigateTelemetry_ForBothAHitAndAPhaseThreeRelation()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");
        EngramMcpTools.Navigate(sandbox.Home, Session, "anything", "callers");

        var records = File.ReadAllLines(Telemetry.ResolvePath(sandbox.Home))
            .Select(Telemetry.TryParse)
            .Where(r => r?.Kind == TelemetryEventKind.Navigate)
            .ToList();

        Assert.Equal(2, records.Count);

        var hit = records[0]!;
        Assert.Equal("defined_at", hit.Relation);
        Assert.True(hit.Found);
        Assert.Contains("Exact", hit.Tiers);

        var notYetIndexed = records[1]!;
        Assert.Equal("callers", notYetIndexed.Relation);
        Assert.False(notYetIndexed.Found);
        Assert.Equal(string.Empty, notYetIndexed.Tiers);
    }

    // R2: proves the LIKE-escaping (S2) does something. Unescaped, "_" is a single-character
    // wildcard, so the query "Foo_ar" would match the symbol "FooBar" as a substring.
    [Fact]
    public void DefinedAt_LiteralUnderscoreInQuery_DoesNotActAsAWildcard()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo", "FooBar.cs", "public sealed class FooBar { }");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Foo_ar", "defined_at");

        Assert.Contains("No symbol named", result);
    }

    // R2, imports path-suffix predicate: unescaped, "A_.cs" would match the path ending in
    // "AB.cs" through the same single-character wildcard.
    [Fact]
    public void Imports_LiteralUnderscoreInQuery_DoesNotActAsAWildcard()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo", "AB.cs", "using System.Text;\n\npublic sealed class AB { }");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "A_.cs", "imports");

        Assert.Contains("No file matching", result);
    }

    // R3: the original LIMIT -1 unbounded bug (S1) lived in the imports path, and no test
    // observed the clamp actually firing at 100 rather than merely not crashing.
    [Fact]
    public void Imports_LimitIsClampedAtOneHundred()
    {
        using var sandbox = new SandboxHome();
        var repoPath = Path.Combine(sandbox.Home.Root, "many-imports-repo");
        Directory.CreateDirectory(repoPath);
        for (var i = 0; i < 105; i++)
        {
            var dir = Path.Combine(repoPath, $"dir{i}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "Common.cs"),
                $"using System.Text;\n\npublic sealed class Common{i} {{ }}");
        }

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repoPath);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Common.cs", "imports", limit: 1_000_000);

        var matchCount = result.Split('\n').Count(line => line.TrimStart().StartsWith("[path-suffix]"));
        Assert.Equal(100, matchCount);
    }

    // Architect follow-up to the §3.5 retraction: nothing pinned CodeIndexer.cs:547-548's
    // EnsureEntity-before-Remember ordering for a nested symbol specifically. Asserts the tier
    // (not just that the symbol was found), since a symbol-identity assertion passes under the
    // substring tier too.
    [Fact]
    public void DefinedAt_NestedSymbol_BareLeafNameReturnsExactTierMatch()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(
            sandbox,
            "fixture-repo",
            "Outer.cs",
            "using System.Text;\n\npublic sealed class Outer\n{\n    public sealed class Inner { }\n}\n");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Inner", "defined_at");

        Assert.Contains("[Exact]", result);
    }

    // The index is eventually consistent with the working tree, never synchronous with it, so a
    // result can describe a file as it was several edits ago. Unmarked, that is indistinguishable
    // from a current answer — and lookup-nudge now steers symbol lookups here first, so the
    // reliance is manufactured by us and the age has to come back with the answer.
    [Fact]
    public void DefinedAt_FileWrittenAfterIndexing_IsMarkedStale()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        // Two seconds past indexed_at, which has second resolution — a write inside the same
        // second is deliberately not treated as evidence.
        var file = Path.Combine(repo, "Program.cs");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.Contains("[stale]", result);
        Assert.Contains("changed on disk after", result);
    }

    // The other half, and the one that makes the marker mean anything: an untouched file must NOT
    // be marked. A test that only asserts [stale] appears would pass just as well if every result
    // were marked, which is exactly as useless as marking none.
    [Fact]
    public void DefinedAt_UntouchedFile_IsNotMarkedStale()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.Contains("#Widget", result);
        Assert.DoesNotContain("[stale]", result);
        Assert.DoesNotContain("changed on disk after", result);
    }

    // Every relation marks staleness, not just defined_at. The absence of a marker is what carries
    // the claim "this is current", so a relation that never checks makes that claim falsely on
    // every line — partial coverage of a freshness signal is worse than none.
    [Theory]
    [InlineData("imports")]
    [InlineData("callers")]
    [InlineData("callees")]
    public void EveryRelation_MarksStaleness_WhenTheFileChangedAfterIndexing(string relation)
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);
        SeedCall(connection, SymbolPath(connection, "Widget"), "Helper");

        File.SetLastWriteTimeUtc(Path.Combine(repo, "Program.cs"), DateTime.UtcNow.AddSeconds(2));

        var query = Query(relation);
        var result = EngramMcpTools.Navigate(sandbox.Home, Session, query, relation);

        Assert.Contains("[stale]", result);
        Assert.Contains("changed on disk after", result);
    }

    [Theory]
    [InlineData("imports")]
    [InlineData("callers")]
    [InlineData("callees")]
    public void EveryRelation_LeavesUntouchedFilesUnmarked(string relation)
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);
        SeedCall(connection, SymbolPath(connection, "Widget"), "Helper");

        var query = Query(relation);
        var result = EngramMcpTools.Navigate(sandbox.Home, Session, query, relation);

        Assert.DoesNotContain("[stale]", result);
        Assert.DoesNotContain("changed on disk after", result);
    }

    // A miss states a fact about the index, and without this it reads as a fact about the
    // repository. Gitignored files are never indexed and recent edits wait for the queue to drain,
    // so "not found here" and "does not exist" are different answers — and the lookup-nudge hook
    // now steers symbol lookups here first, which makes conflating them a wrong conclusion the
    // nudge itself caused.
    [Fact]
    public void DefinedAt_Miss_SaysWhatTheIndexDoesNotCover()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "NoSuchSymbol", "defined_at");

        Assert.Contains("No symbol named", result);
        Assert.Contains("gitignored", result);
        Assert.Contains("Grep", result);
    }

    // The caveat is about coverage. An unknown relation is a usage error — the index was never
    // consulted — so telling the caller to fall back to Grep would answer a question nobody asked.
    [Fact]
    public void UnknownRelation_DoesNotCarryTheCoverageCaveat()
    {
        using var sandbox = new SandboxHome();

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "sideways");

        Assert.Contains("Unknown relation", result);
        Assert.DoesNotContain("gitignored", result);
    }

    private static string Query(string relation) => relation switch
    {
        "imports" => "Program.cs",
        "callers" => "Helper",
        _ => "Widget",
    };

    // The call graph is seeded rather than extracted. C# `calls` facts need the Roslyn sidecar and
    // the TypeScript ones need compiled tree-sitter grammars, so an extracted fixture would skip
    // wherever those are absent — and a freshness test that skips is a freshness claim nobody
    // checked. What is under test is the marking, which only needs a call edge whose caller sits in
    // a file the indexer really recorded.
    private static void SeedCall(SqliteConnection connection, string callerPath, string callee) =>
        FactStore.Remember(
            connection,
            new FactWrite(callerPath, "symbol", "calls", "calls " + callee, "code", "observed",
                Regenerable: true, ObjectPath: CodePaths.ForSymbolName(callee), ObjectKind: "symbol-name"),
            DateTimeOffset.UtcNow);

    private static string SymbolPath(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM entity WHERE path LIKE $suffix;";
        command.Parameters.AddWithValue("$suffix", "%#" + name);
        return (string)command.ExecuteScalar()!;
    }

    private static string CreateFixture(SandboxHome sandbox, string repoDirName) =>
        CreateFixture(sandbox, repoDirName, "Program.cs", ProgramCs);

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
