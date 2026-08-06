using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// The third lane, at the fusion. What matters here is not that a vector rank nudges an order —
/// it is that a fact no lexical lane can reach at all becomes reachable, because that is the only
/// thing the vector lane is for.
/// </summary>
public sealed class RecallEngineVectorTests
{
    private static readonly Dictionary<long, int> None = [];

    /// <summary>
    /// Shares no term with any query below. Term overlap and bm25 both stem and both match
    /// literals, so neither can connect "backoff" to "retry" — an embedding is the only lane that
    /// can, and this fact exists to be unreachable without one.
    /// </summary>
    private static readonly List<CannedFact> Facts =
    [
        new("f7", "uploader", "uses", "The uploader uses exponential backoff.", "code", "delivery", 0),
        new("f9", "vacuum", "runs", "Compaction runs nightly at three.", "ops", "storage", 0),
    ];

    [Fact]
    public void AFactReachableOnlyByVector_IsReturned()
    {
        var without = RecallEngine.Pack("retry policy", Facts, [], [], None, None, 500);
        var with = RecallEngine.Pack("retry policy", Facts, [], [], None, new Dictionary<long, int> { [7] = 1 }, 500);

        Assert.Equal(0, without.FactCount);
        Assert.Contains("[f7]", with.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVectorLane_ChangesTheOrderOfWhatComesBack()
    {
        var nine = RecallEngine.Pack("retry policy", Facts, [], [], None,
            new Dictionary<long, int> { [7] = 2, [9] = 1 }, 500);
        var seven = RecallEngine.Pack("retry policy", Facts, [], [], None,
            new Dictionary<long, int> { [7] = 1, [9] = 2 }, 500);

        Assert.StartsWith("[f9]", FirstFactLine(nine.Text), StringComparison.Ordinal);
        Assert.StartsWith("[f7]", FirstFactLine(seven.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void AVectorRankForAFactNotInTheCorpus_IsIgnoredRatherThanInvented()
    {
        var result = RecallEngine.Pack("retry policy", Facts, [], [], None,
            new Dictionary<long, int> { [4242] = 1 }, 500);

        Assert.Equal(0, result.FactCount);
    }

    [Fact]
    public void WithNoVectorRanks_TheRankingIsWhatItWasBeforeTheLaneExisted()
    {
        // The regression this catches is a third Reciprocal term shifting scores even when the
        // lane contributed nothing, which would retune the other two lanes by accident.
        var withLane = RecallEngine.Pack("uploader backoff", Facts, [], [], None, None, 500);
        var withoutLane = RecallEngine.Pack("uploader backoff", Facts, [], [], None, 500);

        Assert.Equal(withoutLane.Text, withLane.Text);
    }

    [Fact]
    public void AFactFoundByBothLexicalAndVector_OutranksOneFoundByEitherAlone()
    {
        var result = RecallEngine.Pack(
            "retry policy",
            Facts,
            [],
            [],
            new Dictionary<long, int> { [9] = 1 },
            new Dictionary<long, int> { [7] = 1, [9] = 2 },
            500);

        // f9 is rank 1 lexically and rank 2 by vector; f7 is rank 1 by vector and absent lexically.
        // Fusion adds, so agreement between lanes wins — that is the property RRF is chosen for.
        Assert.StartsWith("[f9]", FirstFactLine(result.Text), StringComparison.Ordinal);
    }

    private static string FirstFactLine(string text) =>
        text.Split('\n').First(line => line.TrimStart().StartsWith("[f", StringComparison.Ordinal)).TrimStart();
}
