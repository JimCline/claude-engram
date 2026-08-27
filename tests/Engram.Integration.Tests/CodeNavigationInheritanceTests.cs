using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Acceptance tests for docs/specs/close-graph-query-gap.md §2/§8: syntactic inheritance and
/// containment predicates, the navigate relations that read them, and §1b's hub-ambiguity
/// note on `callers`. Seeds facts directly (the same pattern CodeNavigationPhase3Tests uses
/// for callers/callees) so the query layer is proven independent of a live extractor.
/// </summary>
public sealed class CodeNavigationInheritanceTests
{
    private static readonly McpSessionId Session = new("test-session");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void Implements_ReturnsEveryBaseListEdge_RegardlessOfPredicate()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Widget", "Widget");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "inherits", "Object");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "implements", "Base");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "implements");

        Assert.Contains("[inherits]", result);
        Assert.Contains("Object", result);
        Assert.Contains("[implements]", result);
        Assert.Contains("Base", result);
    }

    // §8.5.3 item 3: a caller asking "implements" must be told when a hit came from a
    // language whose grammar could not tell a base class from an interface.
    [Fact]
    public void Implements_OnADerivesFromResult_CarriesTheOverApproximationNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.cs#Widget", "Widget");
        SeedEdge(connection, "/projects/p/code/r/a.cs#Widget", "derives-from", "IBase");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "implements");

        Assert.Contains("does not distinguish base classes", result);
    }

    [Fact]
    public void Implementers_FindsSubjectsNamingTheQueryAsABase()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "Animal");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Animal", "implementers");

        Assert.Contains("Dog", result);
    }

    [Fact]
    public void Members_ReturnsContainsEdgesForTheType()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Widget", "Widget");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "contains", "run");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "members");

        Assert.Contains("run", result);
    }

    // §1b: an over-approximation trusted rather than sanity-checked under first-reach must
    // say so — a leaf query matching more than one distinct callee spelling is marked.
    [Fact]
    public void Callers_AmbiguousLeaf_NotesHowManyDistinctSpellingsMatched()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Get", "Get");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#caller1", "caller1");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#caller2", "caller2");
        SeedCall(connection, "/projects/p/code/r/a.ts#caller1", "Get");
        SeedCall(connection, "/projects/p/code/r/a.ts#caller2", "cache.Get");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Get", "callers");

        Assert.Contains("matched 2 distinct callee spellings", result);
    }

    [Fact]
    public void Callers_UnambiguousLeaf_CarriesNoHubNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#helper", "helper");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#caller", "caller");
        SeedCall(connection, "/projects/p/code/r/a.ts#caller", "helper");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "helper", "callers");

        Assert.DoesNotContain("distinct callee spellings", result);
    }

    private static void SeedSymbol(SqliteConnection connection, string path, string name)
    {
        using var transaction = EngramDatabase.BeginWrite(connection);
        FactStore.EnsureEntity(connection, transaction, path, "symbol", T0.ToUnixTimeSeconds(), name);
        transaction.Commit();
    }

    private static void SeedCall(SqliteConnection connection, string callerPath, string callee) =>
        FactStore.Remember(
            connection,
            new FactWrite(callerPath, "symbol", "calls", "calls " + callee, "code", "observed",
                Regenerable: true, ObjectPath: CodePaths.ForSymbolName(callee), ObjectKind: "symbol-name"),
            T0);

    private static void SeedEdge(SqliteConnection connection, string subjectPath, string predicate, string objectName) =>
        FactStore.Remember(
            connection,
            new FactWrite(subjectPath, "symbol", predicate, predicate + " " + objectName, "code", "observed",
                Regenerable: true, ObjectPath: CodePaths.ForSymbolName(objectName), ObjectKind: "symbol-name"),
            T0);
}
