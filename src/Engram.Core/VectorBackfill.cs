using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>Why a backfill pass stopped.</summary>
public enum BackfillOutcome
{
    /// <summary>Nothing is left to embed.</summary>
    Completed,

    /// <summary>The batch budget ran out with work remaining. Run again.</summary>
    BatchLimitReached,

    /// <summary>
    /// A whole batch failed to embed, so another pass would re-read the same facts and fail the
    /// same way. Distinct from <see cref="Completed"/> because the queue is not empty.
    /// </summary>
    StalledOnFailures,

    /// <summary>
    /// The index holds vectors from a different model, width, or input composition than this
    /// embedder produces. Nothing was written.
    /// </summary>
    SpaceMismatch,
}

public readonly record struct BackfillResult(
    BackfillOutcome Outcome,
    int Embedded,
    int Failed,
    int Remaining);

/// <summary>
/// Drains <see cref="VectorIndex.ReadBackfillBatch"/> through an <see cref="IEmbedder"/>.
/// </summary>
/// <remarks>
/// Off the write path by construction (D4). Embedding a batch takes long enough that doing it
/// inside a transaction would hold the single writer lock for the duration and stall every hook
/// behind it, so a pass is three separate steps — read a batch, embed with no transaction open,
/// write the results — and the write transaction lives only as long as the inserts.
///
/// <para>That the queue is a query rather than a table is what makes the gap between those
/// steps harmless: a fact superseded mid-pass is simply written with <c>is_live = 0</c>, and a
/// fact deleted mid-pass is skipped, both because <see cref="VectorIndex.Write"/> re-reads
/// liveness at write time instead of trusting what the read step saw.</para>
/// </remarks>
public static class VectorBackfill
{
    public const int DefaultBatchSize = 32;

    /// <summary>
    /// Embeds pending facts until the queue is empty or <paramref name="maxBatches"/> is spent.
    /// </summary>
    public static async Task<BackfillResult> RunAsync(
        SqliteConnection connection,
        IEmbedder embedder,
        int batchSize = DefaultBatchSize,
        int maxBatches = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatches);

        VectorIndex.EnsureCreated(connection, embedder.Space);

        if (VectorIndex.ReadSpace(connection) != embedder.Space
            || VectorIndex.ReadInputVersion(connection) != VectorIndex.InputVersion)
        {
            // Refusing beats rebuilding on its own: a width change would be caught by vec0
            // anyway, but a same-width model swap would not, and silently mixing spaces is the
            // failure D18 names — meaningless distances that look like ordinary numbers. The
            // fix is `embed --rebuild`, which is a decision with a cost, so a human makes it.
            return new BackfillResult(
                BackfillOutcome.SpaceMismatch,
                Embedded: 0,
                Failed: 0,
                Remaining: VectorIndex.CountPending(connection));
        }

        // Before filling, fix. Supersession leaves the index stale by design — see
        // VectorIndex.Reconcile for why that is not FactStore's job — and this is the pass that
        // notices, so it has to notice first or a whole pass ranks retired facts.
        VectorIndex.Reconcile(connection);

        var embedded = 0;
        var failed = 0;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pending = VectorIndex.ReadBackfillBatch(connection, batchSize);
            if (pending.Count == 0)
            {
                return new BackfillResult(BackfillOutcome.Completed, embedded, failed, Remaining: 0);
            }

            var texts = new string[pending.Count];
            for (var i = 0; i < pending.Count; i++)
            {
                texts[i] = pending[i].Text;
            }

            var vectors = await embedder.EmbedAsync(texts, cancellationToken).ConfigureAwait(false);
            if (vectors.Count != pending.Count)
            {
                throw new InvalidOperationException(
                    $"{embedder.GetType().Name} returned {vectors.Count} vectors for "
                    + $"{pending.Count} texts; results are matched to facts by position.");
            }

            var written = 0;
            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                for (var i = 0; i < pending.Count; i++)
                {
                    var vector = vectors[i];
                    if (vector is null)
                    {
                        // Deliberately no row, not a placeholder. A zero vector would leave the
                        // queue and then answer every query at NaN distance; no row means this
                        // fact is simply still pending and gets another try next pass.
                        failed++;
                        continue;
                    }

                    if (vector.Length != embedder.Space.Dimensions)
                    {
                        throw new InvalidOperationException(
                            $"{embedder.GetType().Name} declares {embedder.Space.Dimensions} "
                            + $"dimensions but returned {vector.Length} for fact "
                            + $"{pending[i].FactId}.");
                    }

                    VectorIndex.Write(connection, transaction, pending[i].FactId, vector);
                    written++;
                }

                transaction.Commit();
            }

            embedded += written;

            if (written == 0)
            {
                // Every text in the batch failed, and the queue is ordered, so the next read
                // returns the same facts. Without this the loop spins on a poison batch until
                // maxBatches runs out, burning an embedder call per turn.
                return new BackfillResult(
                    BackfillOutcome.StalledOnFailures,
                    embedded,
                    failed,
                    VectorIndex.CountPending(connection));
            }
        }

        var remaining = VectorIndex.CountPending(connection);
        return new BackfillResult(
            remaining == 0 ? BackfillOutcome.Completed : BackfillOutcome.BatchLimitReached,
            embedded,
            failed,
            remaining);
    }
}
