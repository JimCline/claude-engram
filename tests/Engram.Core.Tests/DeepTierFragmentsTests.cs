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
            new DeepAnalysis("Http.cs", [first, second], [], null, []));

        var declared = Assert.Single(merged, c => c.Predicate == "declared-as");
        Assert.Equal("first", declared.Body);
        Assert.EndsWith("#Http/Get(string key)", declared.EntityPath, StringComparison.Ordinal);
    }
}
