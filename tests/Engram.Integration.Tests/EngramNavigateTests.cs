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

    [Theory]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("neighbors")]
    public void PhaseThreeRelations_AnswerNotYetIndexed_NeverAnEmptyResult(string relation)
    {
        using var sandbox = new SandboxHome();

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "anything", relation);

        Assert.Contains("not yet indexed", result);
    }

    [Fact]
    public void UnknownRelation_IsRejected()
    {
        using var sandbox = new SandboxHome();

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "bogus");

        Assert.Contains("Unknown relation", result);
    }

    [Fact]
    public void Response_CarriesExtractionTierUnrecordedHeader()
    {
        using var sandbox = new SandboxHome();
        var repo = CreateFixture(sandbox, "fixture-repo");
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Index(connection, sandbox, repo);

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "defined_at");

        Assert.Contains("extraction tier not recorded", result);
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
