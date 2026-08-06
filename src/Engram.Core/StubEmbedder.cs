namespace Engram.Core;

/// <summary>
/// A deterministic embedder for tests: no model, no weights, no I/O.
/// </summary>
/// <remarks>
/// D18 requires this to exist before any real provider does, because recall output is
/// model-dependent and tier-2 tests cannot assert on it otherwise. It is built first for the
/// same reason `engram explain` is built before the fusion it explains — a test harness
/// written after the thing it tests gets written against remembered intentions.
///
/// <para><b>What it is honest about.</b> This is a hashing vectorizer: it projects tokens onto
/// dimensions, so two texts are close exactly when they share words. That makes it a faithful
/// stand-in for the *plumbing* — dimension agreement, batch shape, backfill, rebuild, index
/// metadata, fusion mechanics — and a poor one for retrieval *quality*, because the whole
/// point of the vector lane per D18 is finding facts that share no words with the query. A
/// test that asserts the stub bridges vocabulary mismatch is asserting something false. Use
/// <see cref="Scripted"/> for those, where the vectors are stated outright rather than
/// implied by an algorithm that cannot produce them.</para>
///
/// <para><b>Why not <c>string.GetHashCode</c>.</b> It is randomized per process by default in
/// .NET, so a stub built on it is deterministic within one run and not across runs. Every
/// vector would change on restart: fixtures pass locally, the rebuild path looks correct
/// because it also re-embeds, and cross-process tests flake for a reason that reads as
/// concurrency. FNV-1a below is fixed by its constants and cannot drift.</para>
/// </remarks>
public sealed class StubEmbedder : IEmbedder
{
    public const string ModelId = "stub-hashing-v1";
    public const int DefaultDimensions = 1024;

    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private readonly IReadOnlyDictionary<string, float[]> _scripted;

    public StubEmbedder(int dimensions = DefaultDimensions)
        : this(new Dictionary<string, float[]>(StringComparer.Ordinal), dimensions)
    {
    }

    private StubEmbedder(IReadOnlyDictionary<string, float[]> scripted, int dimensions)
    {
        Space = new EmbeddingSpace(ModelId, dimensions);
        _scripted = scripted;
    }

    public EmbeddingSpace Space { get; }

    /// <summary>
    /// An embedder that returns exactly the vectors it is given, and hashes anything else.
    /// </summary>
    /// <remarks>
    /// For the tests the hashing path cannot serve: asserting that a query retrieves a fact
    /// sharing none of its words. Stating those vectors is more honest than tuning a hash
    /// until it appears to understand English.
    /// </remarks>
    public static StubEmbedder Scripted(IReadOnlyDictionary<string, float[]> vectors, int dimensions = DefaultDimensions)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        foreach (var (text, vector) in vectors)
        {
            if (vector.Length != dimensions)
            {
                throw new ArgumentException(
                    $"Scripted vector for \"{text}\" is {vector.Length} wide, but this embedder declares {dimensions}.",
                    nameof(vectors));
            }
        }

        return new StubEmbedder(vectors, dimensions);
    }

    /// <remarks>
    /// Never returns a null element: hashing cannot fail on a well-formed string, and inventing
    /// failures the algorithm does not have would make every caller's null-handling look tested
    /// when it is not. Tests that need the failure path construct it explicitly.
    /// </remarks>
    public Task<IReadOnlyList<float[]?>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = Embed(texts[i]);
        }

        return Task.FromResult<IReadOnlyList<float[]?>>(results);
    }

    private float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_scripted.TryGetValue(text, out var scripted))
        {
            return (float[])scripted.Clone();
        }

        var vector = new float[Space.Dimensions];
        var tokens = 0;

        foreach (var token in Tokenize(text))
        {
            var hash = Fnv1a(token);

            // Signed hashing: the sign bit comes from a different part of the hash than the
            // index, so two tokens colliding on a dimension cancel as often as they compound
            // instead of always inflating it.
            var index = (int)(hash % (ulong)Space.Dimensions);
            var sign = (hash & 0x8000_0000_0000_0000UL) == 0 ? 1f : -1f;

            vector[index] += sign;
            tokens++;
        }

        if (tokens == 0)
        {
            // A zero vector makes cosine similarity NaN, which surfaces later and elsewhere as
            // a ranking that is neither right nor obviously wrong. Empty text gets a real unit
            // vector instead, and every empty text gets the same one.
            vector[0] = 1f;
            return vector;
        }

        Normalize(vector);
        return vector;
    }

    private static void Normalize(float[] vector)
    {
        double sumOfSquares = 0;
        foreach (var component in vector)
        {
            sumOfSquares += (double)component * component;
        }

        // Reachable when signed hashing cancels every token exactly — rare, but it is the one
        // path that would otherwise divide by zero and hand back NaNs.
        if (sumOfSquares == 0)
        {
            vector[0] = 1f;
            return;
        }

        var length = (float)Math.Sqrt(sumOfSquares);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= length;
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWordChar && start < 0)
            {
                start = i;
            }
            else if (!isWordChar && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    private static ulong Fnv1a(string token)
    {
        var hash = FnvOffsetBasis;
        foreach (var c in token)
        {
            hash ^= c;
            hash *= FnvPrime;
        }

        return hash;
    }
}
