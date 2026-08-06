namespace Engram.Core;

/// <summary>
/// Which embedding space a vector belongs to: the model that produced it and how wide it is.
/// </summary>
/// <remarks>
/// This exists because of the quiet failure D18 names. Vectors from different models are not
/// comparable — cosine distance between a Qwen3 vector and a nomic vector is a real number,
/// it is meaningless, and nothing about it looks wrong. Retrieval simply degrades into
/// confident nonsense. Dimensions differ too, so a provider change can invalidate the
/// <c>vec0</c> table itself rather than merely its contents.
///
/// So the space is recorded as index metadata and compared, never assumed. It is deliberately
/// a value type with structural equality: "is this the same space" has to be a cheap, total
/// comparison that no call site can get subtly wrong.
///
/// It is also resolvable without loading a model. `doctor` has to be able to report a
/// mismatch, and spike E measured the load at 6.5 s — a diagnostic that pays that to answer
/// "which space is this" is a diagnostic nobody runs.
/// </remarks>
public readonly record struct EmbeddingSpace
{
    public EmbeddingSpace(string model, int dimensions)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Embedding model identifier must not be blank.", nameof(model));
        }

        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimensions), dimensions, "Embedding dimension must be positive.");
        }

        Model = model;
        Dimensions = dimensions;
    }

    /// <summary>Identifies the model, e.g. <c>qwen3-embedding-0.6b-q8_0</c>.</summary>
    public string Model { get; }

    /// <summary>Vector width. Pinned in <c>schema_meta</c>; a change invalidates the index.</summary>
    public int Dimensions { get; }

    public override string ToString() => $"{Model}/{Dimensions}";
}

/// <summary>
/// Turns text into vectors. The seam D18 anticipated, so a provider change is a construction
/// detail rather than a retrieval rewrite.
/// </summary>
/// <remarks>
/// Absence, not a null object, is how embeddings-disabled is represented. The spec names a
/// <c>NullEmbedder</c>, but a provider that returns empty or zero vectors would let a
/// disabled install write rows into the vector table that look like data and rank like
/// noise. FTS5-only is a fully supported configuration under D18 rather than a degraded one,
/// so every call site already has to handle "there is no embedder" — making that the only
/// representation means the broken middle state cannot be constructed.
///
/// The API is batch-only because embedding is batched by construction: it runs server-side on
/// a queue, off the write path (D4), bounded by <c>[embedding] max_batch</c>. A single-text
/// overload would invite call sites that embed in a loop, which is the shape that turns a
/// throughput problem into a latency one.
/// </remarks>
public interface IEmbedder
{
    /// <summary>
    /// The space this embedder produces into. Must be answerable without loading weights.
    /// </summary>
    EmbeddingSpace Space { get; }

    /// <summary>
    /// Embeds each text, returning one entry per input, in order — a vector
    /// <see cref="EmbeddingSpace.Dimensions"/> wide, or <c>null</c> where that one text could
    /// not be embedded.
    /// </summary>
    /// <remarks>
    /// Implementations must verify the width they actually produce against the width they
    /// declare, and fail rather than return a differently-shaped vector. A provider that
    /// quietly returns 768 floats into a 1024-wide index corrupts every subsequent query,
    /// and does so without an error anywhere.
    ///
    /// <para><b>Why an element may be null.</b> A real provider batches, and a batch can fail
    /// on one input — an overlong text, a tokenizer edge case — while the rest are fine. The
    /// alternatives are both worse. Throwing for the whole batch lets one poison text block
    /// every fact batched with it, permanently, since the backfill will re-batch them together
    /// forever. Returning an empty or zero vector is worse still: zero vectors make cosine
    /// similarity NaN, and the damage surfaces later and elsewhere as a ranking that is neither
    /// right nor obviously wrong.</para>
    ///
    /// <para>Null costs nothing to handle because "no vector" is already the backfill's
    /// representation of work to do — the queue is <c>LEFT JOIN … WHERE v.fact_id IS NULL</c>,
    /// so a fact whose embedding failed simply stays in it and is retried on the next pass,
    /// with no error table and no bookkeeping. Callers must skip nulls rather than store them.
    /// Implementations should still exhaust their own retries first; null is the last word on
    /// one text, not a shrug.</para>
    /// </remarks>
    Task<IReadOnlyList<float[]?>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
