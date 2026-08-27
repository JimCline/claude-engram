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

    // §11.2 (Architect ruling): implementers leaf-matches now, the same as `callers` — a
    // qualified stored spelling must answer a bare query, which exact-string equality
    // (fixed by this change) previously missed silently.
    [Fact]
    public void Implementers_QueryIsBare_MatchesAQualifiedStoredSpelling()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "NS.IFoo");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "IFoo", "implementers");

        Assert.Contains("Dog", result);
    }

    // The mirror case: a qualified query must still find a bare stored spelling.
    [Fact]
    public void Implementers_QueryIsQualified_MatchesABareStoredSpelling()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "IFoo");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "NS.IFoo", "implementers");

        Assert.Contains("Dog", result);
    }

    // Explicitly out of scope per §11.2: leaf matching, not SymbolResolver's substring tier —
    // a query for `IFoo` must not match a stored `IFooBar`, the false-positive class Grep is
    // being replaced for.
    [Fact]
    public void Implementers_DoesNotSubstringMatch()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "IFooBar");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "IFoo", "implementers");

        Assert.DoesNotContain("Dog", result);
    }

    // §11.2 / §1b: leaf matching makes one query hit more than one distinct stored spelling —
    // an object-side over-approximation, marked the same way `callers`' hub note already
    // marks its own leaf-matched ambiguity.
    [Fact]
    public void Implementers_LeafMatchesMoreThanOneSpelling_CarriesTheHubNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedSymbol(connection, "/projects/p/code/r/a.ts#Cat", "Cat");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "IFoo");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Cat", "inherits", "NS.IFoo");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "IFoo", "implementers");

        Assert.Contains("distinct type spellings", result);
        Assert.Contains("Dog", result);
        Assert.Contains("Cat", result);
    }

    // graph-index-audit §2.4 / Ultra-Advisor's windowed-cap ruling: a windowed per-predicate
    // cap (ROW_NUMBER() OVER (PARTITION BY predicate)), not a flat LIMIT — proves both halves
    // at once: the cap actually engages past 1000 rows for one predicate, and a low-volume
    // sibling predicate still gets through rather than being starved out of the fetched set
    // (which would silently break AppendOverApproximationNote's sampling, §8.5.3 item 3).
    [Fact]
    public void Implementers_MoreThan1000EdgesForOnePredicate_CapsPerPredicateAndMarksTruncation()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        for (var i = 0; i < 1001; i++)
        {
            SeedSymbol(connection, $"/projects/p/code/r/a.ts#Sub{i}", $"Sub{i}");
            SeedEdge(connection, $"/projects/p/code/r/a.ts#Sub{i}", "inherits", "Base");
        }

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Other", "Other");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Other", "derives-from", "Base");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Base", "implementers", limit: 2000);

        Assert.Contains("more than 1000", result);
        Assert.Contains("[derives-from]", result);
        var inheritsCount = result.Split("[inherits]", StringSplitOptions.None).Length - 1;
        Assert.True(inheritsCount <= 1000, $"expected at most 1000 '[inherits]' lines, found {inheritsCount}");
    }

    [Fact]
    public void Implementers_FewerThan1000Edges_CarriesNoTruncationNote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SeedSymbol(connection, "/projects/p/code/r/a.ts#Dog", "Dog");
        SeedEdge(connection, "/projects/p/code/r/a.ts#Dog", "inherits", "Animal");

        var result = EngramMcpTools.Navigate(sandbox.Home, Session, "Animal", "implementers");

        Assert.DoesNotContain("more than 1000", result);
    }

    // §11.1 (Architect ruling): a flag true on every shipped language row is a constant, not
    // a per-result discriminator — the caveat now lives in engram_navigate's static
    // Description, never in a per-result note, and carries no spec-section citation.
    [Fact]
    public void NavigateDescription_StatesTheNestedTypeLimitation_WithNoSpecSectionCitation()
    {
        var method = typeof(EngramMcpTools).GetMethod(nameof(EngramMcpTools.Navigate));
        var description = Assert.IsType<System.ComponentModel.DescriptionAttribute>(
            Assert.Single(method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)));

        Assert.Contains("nested", description.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("§", description.Description, StringComparison.Ordinal);
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
