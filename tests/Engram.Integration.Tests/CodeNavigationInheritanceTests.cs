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

    // F2 (review of graph-enhance): limit was collected via SymbolResolver.Resolve but
    // never applied to the base-list edges actually printed, so a caller asking for 1 got
    // every edge regardless.
    [Fact]
    public void Implements_MoreEdgesThanLimit_ReportsWhatItActuallyShowed()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Widget", "Widget");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "inherits", "Base1");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "inherits", "Base2");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "implements", "Iface1");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "implements", limit: 1);

        Assert.Contains("1 base-list edge", result);
        Assert.Contains("(showing 1 of 3)", result);
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

    // F6 (§8.5.3 item 4 / §10.2, Architect ruling): implementers' exact-match-only design
    // is a known, statically-declared gap for this relation — it must surface whenever the
    // query's own spelling shows it could have been affected, on a hit as much as a miss.
    [Fact]
    public void Implementers_QueryLooksGeneric_DeclaresTheExactMatchGap_OnAHit()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "Comparer<T>");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Comparer<T>", "implementers");

        Assert.Contains("exact spelling only", result);
    }

    [Fact]
    public void Implementers_QueryLooksGeneric_DeclaresTheExactMatchGap_OnATotalMiss()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Comparer<T>", "implementers");

        Assert.Contains("differently", result);
    }

    // F6a (second review of graph-enhance): the dominant case is the query spelled bare
    // against a candidate stored with type arguments, e.g. `implementers IComparer` against
    // a base-list entry spelled `IComparer<T>`. The query itself carries no marker, so
    // checking the query alone never fires exactly when the caveat is most needed.
    [Fact]
    public void Implementers_CandidateCarriesTypeArguments_QueryDoesNot_StillDeclaresTheExactMatchGap()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "IComparer<T>");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "IComparer", "implementers");

        Assert.Contains("differently parameterized", result);
    }

    [Fact]
    public void Implementers_QueryIsNotGeneric_CarriesNoExactMatchGapNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "Animal");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Animal", "implementers");

        Assert.DoesNotContain("differently parameterized", result);
    }

    // F3 (review of graph-enhance): the header reported every matched row (up to 3x limit,
    // one query per predicate) while only `limit` rows actually printed — a mismatch. The
    // header must describe what was displayed, not what was matched.
    [Fact]
    public void Implementers_MoreThanLimit_ReportsWhatItActuallyShowed()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#Cat", "Cat");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#Bird", "Bird");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "Animal");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Cat", "inherits", "Animal");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Bird", "implements", "Animal");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Animal", "implementers", limit: 1);

        Assert.Contains("1 implementer", result);
        Assert.Contains("(showing 1 of 3)", result);
    }

    // F6b (second review of graph-enhance): the second required static gap class, distinct
    // from the generics one above — DeepTier.Merge only resolves inheritance and containment
    // against a file's top-level declarations (§8.6), so a nested type's own edges are
    // silently dropped for every language F5 made this universal for. Declared per
    // LanguageDefinition row, fired whenever a displayed result belongs to such a language.
    [Fact]
    public void Implements_ResultFromANestedTypeDroppingLanguage_CarriesTheGapNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Widget", "Widget");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "inherits", "Base");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "implements");

        Assert.Contains("nested-type declarations", result);
    }

    [Fact]
    public void Implementers_ResultFromANestedTypeDroppingLanguage_CarriesTheGapNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "Animal");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Animal", "implementers");

        Assert.Contains("nested-type declarations", result);
    }

    [Fact]
    public void Implements_ResultFromALanguageNotDeclaringTheGap_CarriesNoGapNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.md#Widget", "Widget");
        SeedEdge(connection, "/projects/p/code/r/a.md#Widget", "derives-from", "Base");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "implements");

        Assert.DoesNotContain("nested-type declarations", result);
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

    [Fact]
    public void Members_ResultFromANestedTypeDroppingLanguage_CarriesTheGapNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Widget", "Widget");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "contains", "run");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "members");

        Assert.Contains("nested-type declarations", result);
    }

    // F2: same limit-ignored defect as Implements, on the members path.
    [Fact]
    public void Members_MoreThanLimit_ReportsWhatItActuallyShowed()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Widget", "Widget");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "contains", "run");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "contains", "stop");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Widget", "contains", "reset");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Widget", "members", limit: 1);

        Assert.Contains("1 member", result);
        Assert.Contains("(showing 1 of 3)", result);
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
