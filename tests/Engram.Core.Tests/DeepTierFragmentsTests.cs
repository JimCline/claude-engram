using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// Grammar v2's fragment rules (D48), held at the one place both deep tiers compose
/// addresses. The conformance suites exercise these end to end through real grammars and
/// the real sidecar; here each rule is pinned in isolation so a break names itself.
/// </summary>
public class DeepTierFragmentsTests
{
    private static DeepSymbol Symbol(string name, string? scope = null, string? parameters = null) =>
        new(name, "symbol", "decl " + name, Doc: null, Scope: scope, Params: parameters);

    [Fact]
    public void AUniqueName_KeepsItsBareForm_EvenWithParameters()
    {
        var fragments = DeepTier.Fragments([Symbol("Get", "Http", "(string key)")]);

        Assert.Equal("Http/Get", Assert.Single(fragments).Fragment);
    }

    [Fact]
    public void ScopeJoins_WithASlash_AndTopLevelHasNone()
    {
        var fragments = DeepTier.Fragments([Symbol("Remember", "FactStore"), Symbol("FactStore")]);

        Assert.Equal(["FactStore/Remember", "FactStore"], fragments.Select(f => f.Fragment));
    }

    [Fact]
    public void Overloads_SplitByTheirWrittenParameterLists()
    {
        var fragments = DeepTier.Fragments(
        [
            Symbol("Get", "Http", "(string key)"),
            Symbol("Get", "Http", "(string key, int count)"),
        ]);

        Assert.Equal(
            ["Http/Get(string key)", "Http/Get(string key, int count)"],
            fragments.Select(f => f.Fragment));
    }

    [Fact]
    public void TheSuffix_CollapsesWhitespaceRuns_SoFormattingIsNotAnAddress()
    {
        var fragments = DeepTier.Fragments(
        [
            Symbol("Get", "Http", "(string key,\n        int count)"),
            Symbol("Get", "Http", "(bool deep)"),
        ]);

        Assert.Equal("Http/Get(string key, int count)", fragments[0].Fragment);
    }

    [Fact]
    public void CollisionsAreScoped_TheSameNameInTwoTypes_NeverSuffixes()
    {
        var fragments = DeepTier.Fragments(
        [
            Symbol("Run", "Server", "()"),
            Symbol("Run", "Client", "()"),
        ]);

        Assert.Equal(["Server/Run", "Client/Run"], fragments.Select(f => f.Fragment));
    }

    [Fact]
    public void AParameterlessCollider_StaysBare_WhileTheCallableTakesTheSuffix()
    {
        var fragments = DeepTier.Fragments([Symbol("C"), Symbol("C", parameters: "(x)")]);

        Assert.Equal(["C", "C(x)"], fragments.Select(f => f.Fragment));
    }

    [Fact]
    public void AResidualCollision_SharesOneAddress_AndMergeKeepsTheFirst()
    {
        // Same scope, name, and written parameters — a syntactic view cannot separate
        // them, so they share an address and the first declaration wins, the same rule
        // partial classes already had.
        var first = Symbol("Get", "Http", "(string key)") with { Declaration = "first" };
        var second = Symbol("Get", "Http", "(string key)") with { Declaration = "second" };

        var merged = DeepTier.Merge(
            "/projects/p/code/r/Http.cs",
            [],
            new DeepAnalysis("Http.cs", [first, second], [], null, [], [], Tier: 1));

        var declared = Assert.Single(merged, c => c.Predicate == "declared-as");
        Assert.Equal("first", declared.Body);
        Assert.EndsWith("#Http/Get(string key)", declared.EntityPath, StringComparison.Ordinal);
    }

    // code-navigation Phase 4 spec §9 item 4: Merge stamps by producer, not uniformly — the
    // carried-over tier-0 file-level "about" keeps AnalyzerTier 0 while the deep-tier-sourced
    // declared-as and calls candidates take analysis.Tier, in one merged result. Falsify by
    // stamping the whole returned list with one tier (e.g. always analysis.Tier): the "about"
    // assertion reddens because tier 0 is no longer the file-level candidate's tier.
    [Fact]
    public void Merge_StampsTheCarriedOverAboutAtTierZero_AndDeepCandidatesAtTheAnalysisTier()
    {
        var tierZero = new List<CodeCandidate>
        {
            new("/projects/p/code/r/Widget.cs", "file", "Widget.cs", "about", "A widget."),
        };
        var symbol = Symbol("Run") with { Declaration = "public void Run()" };
        var analysis = new DeepAnalysis(
            "Widget.cs",
            [symbol],
            [],
            null,
            [new DeepCall("Run", "Helper", 1)],
            [],
            Tier: 2);

        var merged = DeepTier.Merge("/projects/p/code/r/Widget.cs", tierZero, analysis);

        var about = Assert.Single(merged, c => c.Predicate == "about" && c.Kind == "file");
        Assert.Equal(0, about.AnalyzerTier);

        var declared = Assert.Single(merged, c => c.Predicate == "declared-as");
        Assert.Equal(2, declared.AnalyzerTier);

        var call = Assert.Single(merged, c => c.Predicate == "calls");
        Assert.Equal(2, call.AnalyzerTier);
    }

    // item 5: the per-file error path stamps tier 0 for the whole file, never the attempted
    // tier — falsify by having the error path return tierZero with AnalyzerTier overwritten to
    // analysis.Tier, which reddens this assertion.
    [Fact]
    public void Merge_OnAPerFileError_KeepsEveryCarriedCandidateAtTierZero()
    {
        var tierZero = new List<CodeCandidate>
        {
            new("/projects/p/code/r/Widget.cs", "file", "Widget.cs", "about", "A widget."),
        };
        var analysis = new DeepAnalysis("Widget.cs", [], [], "parse error", [], [], Tier: 2);

        var merged = DeepTier.Merge("/projects/p/code/r/Widget.cs", tierZero, analysis);

        Assert.Equal(0, Assert.Single(merged).AnalyzerTier);
    }

    // F4 (review of graph-enhance): an overload set shares one name and one container, so it
    // would otherwise produce that many byte-identical `contains` candidates for the same
    // (container, member) pair — which the indexer's live-fact key cannot tell apart, so
    // writing more than one closes and reinserts a fact identical to itself, a spurious
    // supersession CLAUDE.md's append-only-facts invariant forbids. Falsify by removing the
    // dedup guard in DeepTier.Merge: this count goes from 1 to 3.
    [Fact]
    public void Merge_OverloadsOfOneMember_YieldOnlyOneContainsCandidate()
    {
        var container = Symbol("Http");
        var overloads = new[]
        {
            Symbol("Get", "Http", "()"),
            Symbol("Get", "Http", "(string key)"),
            Symbol("Get", "Http", "(string key, int count)"),
        };

        var analysis = new DeepAnalysis(
            "Http.cs", [container, .. overloads], [], null, [], [], Tier: 1);

        var merged = DeepTier.Merge("/projects/p/code/r/Http.cs", [], analysis);

        var contains = Assert.Single(merged, c => c.Predicate == "contains");
        Assert.Equal("Get", contains.Object);
    }

    // item 6 / §7.1: a missing deep tier stamps tier 0, never the registry's entitled tier —
    // Merge takes its tier from analysis.Tier alone, so a caller that never ran a deep analyzer
    // (tierZero only, no Merge call) must not read as anything but tier 0. Falsify by defaulting
    // CodeCandidate.AnalyzerTier to something other than 0.
    [Fact]
    public void ATierZeroOnlyCandidate_DefaultsToAnalyzerTierZero_NotARegistryEntitlement()
    {
        var candidate = new CodeCandidate(
            "/projects/p/code/r/Widget.cs", "file", "Widget.cs", "about", "A widget.");

        Assert.Equal(0, candidate.AnalyzerTier);
    }
}
