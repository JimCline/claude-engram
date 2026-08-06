using Engram.Core;

namespace Engram.Core.Tests;

public class EmbeddingModelsTests
{
    /// <summary>
    /// The question at install is not "which model is best" but "what will this machine run
    /// without the user turning the feature off", so there has to be more than one answer.
    /// </summary>
    [Fact]
    public void OffersAtLeastThreeRungs()
    {
        Assert.True(EmbeddingModels.All.Count >= 3);
    }

    [Fact]
    public void RungsAreOrderedSmallestFirst()
    {
        // The installer prints this list in order, and "smallest first" is what makes the list
        // legible as a ladder rather than a menu.
        var sizes = EmbeddingModels.All.Select(m => m.ApproximateBytes).ToArray();
        Assert.Equal(sizes.OrderBy(b => b), sizes);

        var widths = EmbeddingModels.All.Select(m => m.Dimensions).ToArray();
        Assert.Equal(widths.OrderBy(w => w), widths);
    }

    [Fact]
    public void EveryRungIsDistinctAndDescribed()
    {
        Assert.Equal(
            EmbeddingModels.All.Count,
            EmbeddingModels.All.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var model in EmbeddingModels.All)
        {
            Assert.NotEmpty(model.DisplayName);
            Assert.NotEmpty(model.Languages);
            Assert.True(model.Dimensions > 0);
            Assert.True(model.ContextTokens > 0);
            Assert.True(model.ApproximateBytes > 0);

            // The tradeoff sentence is the whole point of the registry — a list of names with
            // no consequences attached is not a choice anyone can make.
            Assert.True(
                model.Tradeoff.Length > 60,
                $"{model.Id} has no usable tradeoff description.");
        }
    }

    /// <summary>
    /// A registry row with an invented hash is worse than one with no hash, because the first
    /// looks verified. Nothing may be fetched until a digest has been checked against a real
    /// download.
    /// </summary>
    [Fact]
    public void NoRungClaimsToBeFetchableWithoutAPinnedDigest()
    {
        foreach (var model in EmbeddingModels.All)
        {
            if (model.Source is { } source)
            {
                Assert.Equal(model.IsFetchable, source.Sha256 is { Length: 64 });
            }
            else
            {
                Assert.False(model.IsFetchable);
            }
        }
    }

    [Fact]
    public void TheDefaultIsOneOfTheRungs()
    {
        Assert.NotNull(EmbeddingModels.Find(EmbeddingModels.DefaultId));
        Assert.Equal(EmbeddingModels.DefaultId, EmbeddingModels.Default.Id);
    }

    /// <summary>
    /// A default is a guess about a machine nobody has measured, so it is the rung whose worst
    /// case is mildest — neither the largest download nor the weakest recall.
    /// </summary>
    [Fact]
    public void TheDefaultIsNeitherTheSmallestNorTheLargest()
    {
        Assert.NotEqual(EmbeddingModels.DefaultId, EmbeddingModels.All[0].Id);
        Assert.NotEqual(EmbeddingModels.DefaultId, EmbeddingModels.All[^1].Id);
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndTotal()
    {
        Assert.NotNull(EmbeddingModels.Find("QWEN3-EMBEDDING-0.6B"));
        Assert.Null(EmbeddingModels.Find("nope"));
        Assert.Null(EmbeddingModels.Find(null));
    }

    [Theory]
    [InlineData(25_000_000, "25 MB")]
    [InlineData(610_000_000, "610 MB")]
    [InlineData(1_200_000_000, "1.2 GB")]
    public void SizesAreLabelledForAHuman(long bytes, string expected)
    {
        var model = EmbeddingModels.Default with { ApproximateBytes = bytes };

        Assert.Equal(expected, model.SizeLabel);
    }
}
