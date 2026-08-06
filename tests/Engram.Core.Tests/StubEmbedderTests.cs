using Engram.Core;

namespace Engram.Core.Tests;

public class StubEmbedderTests
{
    private static readonly string[] Corpus =
    [
        "every write is BEGIN IMMEDIATE",
        "the home directory is resolved by EngramHome",
    ];

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static async Task<float[]> EmbedOne(IEmbedder embedder, string text)
    {
        var vectors = await embedder.EmbedAsync([text], TestContext.Current.CancellationToken);

        // EmbedAsync may return null per element when a real provider fails on one text. The
        // stub has no failure path, so asserting here states that contract rather than
        // suppressing the warning and letting a future stub start returning nulls unnoticed.
        Assert.NotNull(vectors[0]);
        return vectors[0]!;
    }

    // The guard that actually matters, and the one same-process determinism cannot give.
    // string.GetHashCode is randomized per process, so a stub built on it produces different
    // vectors on every restart while passing every within-run equality check. Pinning the
    // hash output means that substitution fails here rather than as an intermittent
    // cross-process flake later.
    [Fact]
    public async Task Embed_HashIsPinned_SoAProcessRandomizedHashCannotBeSubstituted()
    {
        var vector = await EmbedOne(new StubEmbedder(dimensions: 64), "every write is BEGIN IMMEDIATE");

        var occupied = Enumerable.Range(0, vector.Length)
            .Where(i => vector[i] != 0f)
            .ToArray();

        // Five tokens, five distinct dimensions of 64, no collisions. These numbers are FNV-1a
        // and nothing else: change the hash and this fails, which is the entire purpose.
        Assert.Equal([21, 30, 36, 58, 60], occupied);
    }

    [Fact]
    public async Task Embed_SameText_ProducesTheSameVector()
    {
        var embedder = new StubEmbedder();

        var first = await EmbedOne(embedder, Corpus[0]);
        var second = await EmbedOne(embedder, Corpus[0]);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Embed_TwoEmbedders_AgreeWithEachOther()
    {
        var first = await EmbedOne(new StubEmbedder(), Corpus[0]);
        var second = await EmbedOne(new StubEmbedder(), Corpus[0]);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Embed_ProducesVectorsOfTheDeclaredWidth()
    {
        var embedder = new StubEmbedder(dimensions: 128);

        var vector = await EmbedOne(embedder, Corpus[0]);

        Assert.Equal(128, embedder.Space.Dimensions);
        Assert.Equal(128, vector.Length);
    }

    [Fact]
    public async Task Embed_ReturnsUnitVectors()
    {
        var vector = await EmbedOne(new StubEmbedder(), Corpus[0]);

        var length = Math.Sqrt(vector.Sum(v => (double)v * v));
        Assert.Equal(1.0, length, precision: 5);
    }

    // The one retrieval-shaped property a hashing vectorizer honestly has. Anything beyond
    // this — a query matching a fact that shares no words — is what Scripted exists for.
    [Fact]
    public async Task Embed_SharedWords_RankAheadOfDisjointOnes()
    {
        var embedder = new StubEmbedder();

        var query = await EmbedOne(embedder, "every write is BEGIN IMMEDIATE");
        var overlapping = await EmbedOne(embedder, "every write uses BEGIN IMMEDIATE transactions");
        var disjoint = await EmbedOne(embedder, "the primer budget caps at three hundred tokens");

        Assert.True(
            Cosine(query, overlapping) > Cosine(query, disjoint),
            $"overlapping {Cosine(query, overlapping):F4} should beat disjoint {Cosine(query, disjoint):F4}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ---")]
    public async Task Embed_TextWithNoTokens_IsAUnitVectorRatherThanZeros(string text)
    {
        var vector = await EmbedOne(new StubEmbedder(), text);

        // Zeros would make every cosine against this NaN, which does not surface as an error
        // anywhere — it surfaces as a ranking that is neither right nor obviously wrong.
        Assert.Contains(vector, v => v != 0f);
        Assert.All(vector, v => Assert.False(float.IsNaN(v)));
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(v => (double)v * v)), precision: 5);
    }

    [Fact]
    public async Task Embed_Batch_ReturnsOneVectorPerInputInOrder()
    {
        var embedder = new StubEmbedder();

        var batch = await embedder.EmbedAsync(Corpus, TestContext.Current.CancellationToken);

        Assert.Equal(Corpus.Length, batch.Count);
        Assert.Equal(await EmbedOne(embedder, Corpus[0]), batch[0]);
        Assert.Equal(await EmbedOne(embedder, Corpus[1]), batch[1]);
    }

    [Fact]
    public async Task Embed_EmptyBatch_ReturnsNothingRatherThanThrowing()
    {
        var batch = await new StubEmbedder().EmbedAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(batch);
    }

    [Fact]
    public async Task Scripted_ReturnsTheVectorItWasGiven()
    {
        var stated = new float[8];
        stated[3] = 1f;
        var embedder = StubEmbedder.Scripted(
            new Dictionary<string, float[]>(StringComparer.Ordinal) { ["son is Liam"] = stated },
            dimensions: 8);

        var vector = await EmbedOne(embedder, "son is Liam");

        Assert.Equal(stated, vector);
    }

    [Fact]
    public async Task Scripted_UnknownText_FallsBackToHashing()
    {
        var embedder = StubEmbedder.Scripted(
            new Dictionary<string, float[]>(StringComparer.Ordinal) { ["son is Liam"] = new float[8] { 1, 0, 0, 0, 0, 0, 0, 0 } },
            dimensions: 8);

        var vector = await EmbedOne(embedder, "something nobody scripted");

        Assert.Equal(8, vector.Length);
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(v => (double)v * v)), precision: 5);
    }

    // A scripted fixture handed out by reference would let one test's mutation reach another's
    // expectations, and the failure would land in whichever test happened to run second.
    [Fact]
    public async Task Scripted_HandsOutACopy_SoACallerCannotMutateTheFixture()
    {
        var stated = new float[4] { 1, 0, 0, 0 };
        var embedder = StubEmbedder.Scripted(
            new Dictionary<string, float[]>(StringComparer.Ordinal) { ["fact"] = stated },
            dimensions: 4);

        var first = await EmbedOne(embedder, "fact");
        first[0] = 99f;
        var second = await EmbedOne(embedder, "fact");

        Assert.Equal(1f, second[0]);
        Assert.Equal(1f, stated[0]);
    }

    [Fact]
    public void Scripted_VectorOfTheWrongWidth_IsRejectedAtConstruction()
    {
        var mismatched = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["fact"] = new float[768],
        };

        var error = Assert.Throws<ArgumentException>(() => StubEmbedder.Scripted(mismatched, dimensions: 1024));
        Assert.Contains("768", error.Message, StringComparison.Ordinal);
        Assert.Contains("1024", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Space_IdentifiesTheModelAndWidth()
    {
        var embedder = new StubEmbedder(dimensions: 256);

        Assert.Equal(StubEmbedder.ModelId, embedder.Space.Model);
        Assert.Equal(256, embedder.Space.Dimensions);
    }
}

public class EmbeddingSpaceTests
{
    // Structural equality is the whole reason this is a value type: "is this the same space"
    // is asked on every read of the index, and a reference comparison would answer no while
    // looking correct.
    [Fact]
    public void Equality_IsStructural()
    {
        Assert.Equal(new EmbeddingSpace("qwen3", 1024), new EmbeddingSpace("qwen3", 1024));
        Assert.NotEqual(new EmbeddingSpace("qwen3", 1024), new EmbeddingSpace("nomic", 1024));

        // The case D18 calls out as invalidating the vec0 table rather than merely its
        // contents, so it must not compare equal to anything.
        Assert.NotEqual(new EmbeddingSpace("qwen3", 1024), new EmbeddingSpace("qwen3", 768));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Construction_RejectsABlankModel(string model)
    {
        Assert.Throws<ArgumentException>(() => new EmbeddingSpace(model, 1024));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Construction_RejectsANonPositiveWidth(int dimensions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmbeddingSpace("qwen3", dimensions));
    }

    [Fact]
    public void ToString_NamesBothHalves()
    {
        Assert.Equal("qwen3/1024", new EmbeddingSpace("qwen3", 1024).ToString());
    }
}
