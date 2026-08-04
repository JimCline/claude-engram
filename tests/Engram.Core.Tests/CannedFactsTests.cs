using System.Text.RegularExpressions;

namespace Engram.Core.Tests;

public class CannedFactsTests
{
    private const int MaxBodyTokens = 70;

    private static readonly Regex IdPattern = new(@"^f\d{3}$", RegexOptions.Compiled);

    [Fact]
    public void AllFactBodies_StayUnderTokenCeiling()
    {
        var oversized = CannedFacts.All
            .Where(f => TokenEstimator.Estimate(f.Body) > MaxBodyTokens)
            .Select(f => $"{f.Id} ({TokenEstimator.Estimate(f.Body)} tokens)")
            .ToList();

        Assert.True(oversized.Count == 0, "Facts over the token ceiling:\n" + string.Join('\n', oversized));
    }

    [Fact]
    public void All_HasExpectedFactCount()
    {
        Assert.Equal(51, CannedFacts.All.Count);
    }

    [Fact]
    public void AllIds_AreUniqueAndWellFormed()
    {
        var ids = CannedFacts.All.Select(f => f.Id).ToList();

        var malformed = ids.Where(id => !IdPattern.IsMatch(id)).ToList();
        Assert.True(malformed.Count == 0, "Malformed ids:\n" + string.Join('\n', malformed));

        var duplicates = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate ids:\n" + string.Join('\n', duplicates));
    }

    [Fact]
    public void Recall_HookEnvelopeQuery_ReturnsSubagentStartEnvelopeFactFirst()
    {
        var ranked = RecallEngine.Rank("hookSpecificOutput additionalContext envelope", CannedFacts.All);

        Assert.NotEmpty(ranked);
        Assert.Equal("f001", ranked[0].Fact.Id);
    }

    [Fact]
    public void Recall_ConcurrentAppendsQuery_ReturnsFileModeAppendFactFirst()
    {
        var ranked = RecallEngine.Rank("concurrent appends losing writes", CannedFacts.All);

        Assert.NotEmpty(ranked);
        Assert.Equal("f038", ranked[0].Fact.Id);
    }

    [Fact]
    public void Recall_SqlitePragmaQuery_ReturnsForeignKeysFactFirst()
    {
        var ranked = RecallEngine.Rank("sqlite foreign keys not enforced", CannedFacts.All);

        Assert.NotEmpty(ranked);
        Assert.Equal("f040", ranked[0].Fact.Id);
    }
}
