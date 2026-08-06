using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Deterministic, and able to fail the way a real provider fails: per text, not per batch.
/// </summary>
/// <remarks>
/// Shared rather than nested in one test class, because the rebuild tests need the same three
/// levers the backfill tests do — a model name, a width, and a way to refuse a specific text —
/// and a second copy would be free to drift from the behaviour the first one asserts against.
/// </remarks>
internal sealed class ScriptedEmbedder(
    string model = "test-embedder",
    int dimensions = ScriptedEmbedder.DefaultDimensions,
    Func<string, bool>? fails = null,
    int? actualWidth = null) : IEmbedder
{
    public const int DefaultDimensions = 4;

    public EmbeddingSpace Space { get; } = new(model, dimensions);

    public int Calls { get; private set; }

    public Task<IReadOnlyList<float[]?>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        Calls++;
        var width = actualWidth ?? Space.Dimensions;
        var vectors = new float[]?[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            if (fails?.Invoke(texts[i]) == true)
            {
                continue;
            }

            var vector = new float[width];
            vector[0] = 1f;
            vector[Math.Abs(texts[i].GetHashCode(StringComparison.Ordinal)) % width] += 0.5f;
            vectors[i] = vector;
        }

        return Task.FromResult<IReadOnlyList<float[]?>>(vectors);
    }
}
