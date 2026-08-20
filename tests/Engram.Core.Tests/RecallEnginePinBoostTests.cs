namespace Engram.Core.Tests;

/// <summary>
/// Tier 1 for per-session pin (docs/memory-expansion/04-lifecycle-spec.md):
/// <see cref="RecallEngine.ApplyPinBoost"/> as a pure function over a fabricated candidate
/// list. D44 requires a pin to be a tie-break among already-matched candidates, never a way
/// to inject a fact a lane did not surface — the falsification below is exactly that
/// distinction.
/// </summary>
public class RecallEnginePinBoostTests
{
    private static RecallCandidate Candidate(
        long factId, string line, FactOrigin origin = FactOrigin.LongTerm) =>
        new(factId, $"f{factId}", line, Fused: 1.0, OverlapRank: 1, LexicalRank: 1, VectorRank: null,
            origin, Tokens: 10, Packed: true);

    [Fact]
    public void NoPins_ReturnsTheInputUnchanged()
    {
        var candidates = new List<RecallCandidate> { Candidate(1, "[f1] one") };

        var result = RecallEngine.ApplyPinBoost(candidates, new HashSet<long>());

        Assert.Same(candidates, result);
    }

    /// <summary>
    /// The falsification the spec names: an irrelevant query — one that matched no lane, so
    /// the pinned fact never entered the candidate list at all — must not gain it back through
    /// the pin. Pinning a fact id absent from <c>candidates</c> must not change the count or
    /// introduce a new entry; only reordering/marking of facts already present is permitted.
    /// </summary>
    [Fact]
    public void APinnedFactAbsentFromTheCandidateList_IsNeverAddedToTheResult()
    {
        var candidates = new List<RecallCandidate> { Candidate(1, "[f1] one") };

        var result = RecallEngine.ApplyPinBoost(candidates, new HashSet<long> { 999 });

        Assert.Single(result);
        Assert.Equal(1, result[0].FactId);
        Assert.False(result[0].Pinned);
    }

    [Fact]
    public void APinnedMatchedCandidate_RisesToTheTopOfItsOriginTier()
    {
        var candidates = new List<RecallCandidate>
        {
            Candidate(1, "[f1] one"),
            Candidate(2, "[f2] two"),
            Candidate(3, "[f3] three"),
        };

        var result = RecallEngine.ApplyPinBoost(candidates, new HashSet<long> { 3 });

        Assert.Equal(3, result[0].FactId);
        Assert.True(result[0].Pinned);
        Assert.Equal(new long?[] { 3, 1, 2 }, result.Select(c => c.FactId));
    }

    [Fact]
    public void APinnedCandidate_GetsThePinnedMarkerAppendedToItsLine()
    {
        var candidates = new List<RecallCandidate> { Candidate(1, "[f1] one thing (session)") };

        var result = RecallEngine.ApplyPinBoost(candidates, new HashSet<long> { 1 });

        Assert.Equal("[f1] one thing (session · pinned)", result[0].Line);
    }

    [Fact]
    public void APinnedCandidateWithNoTrailingParen_AppendsTheMarkerAsItsOwnClause()
    {
        var candidates = new List<RecallCandidate> { Candidate(1, "[f1] one thing") };

        var result = RecallEngine.ApplyPinBoost(candidates, new HashSet<long> { 1 });

        Assert.Equal("[f1] one thing · pinned", result[0].Line);
    }

    /// <summary>
    /// D44/D60: pin reorders within a tier, it never lets a long-term or prior-session pin
    /// cut ahead of an unpinned current-session fact — the two-tier grouping RecallRanker's
    /// SQL and RecallEngine.BuildCandidates both use.
    /// </summary>
    [Fact]
    public void APinInAnOlderTier_NeverOutranksAnUnpinnedCurrentSessionFact()
    {
        var candidates = new List<RecallCandidate>
        {
            Candidate(1, "[f1] current", FactOrigin.CurrentSession),
            Candidate(2, "[f2] long-term", FactOrigin.LongTerm),
        };

        var result = RecallEngine.ApplyPinBoost(candidates, new HashSet<long> { 2 });

        Assert.Equal(new long?[] { 1, 2 }, result.Select(c => c.FactId));
    }
}
