using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>What a rebuild has to do to the index that is there now.</summary>
public enum RebuildAction
{
    /// <summary>No index exists. Nothing is discarded and this is a first build.</summary>
    Build,

    /// <summary>
    /// Same model, width and input composition. The table's shape is right and its rows are not,
    /// so the rows go and the table stays.
    /// </summary>
    Clear,

    /// <summary>
    /// Model, width or input composition moved, so the table itself is wrong — its DDL embeds the
    /// width, and its pinned model is what <see cref="VectorBackfill"/> compares against.
    /// </summary>
    Recreate,
}

/// <summary>What a rebuild would discard and what it would cost, decided before anything moves.</summary>
public sealed record RebuildPlan(
    RebuildAction Action,
    EmbeddingSpace? Current,
    string? CurrentInput,
    EmbeddingSpace Target,
    string TargetInput,
    int Discarded,
    int ToEmbed)
{
    /// <summary>Why the table cannot be kept, or null when it can.</summary>
    public string? Reason =>
        Action is not RebuildAction.Recreate ? null
        : Current is not { } held ? "the index records no model, so what it holds cannot be trusted"
        : held.Dimensions != Target.Dimensions ? $"width moved, {held.Dimensions} -> {Target.Dimensions}"
        : !string.Equals(held.Model, Target.Model, StringComparison.Ordinal) ? $"model moved, {held.Model} -> {Target.Model}"
        : $"input composition moved, {CurrentInput ?? "unrecorded"} -> {TargetInput}";
}

/// <summary>How a rebuild ended, and what it produced.</summary>
public sealed record RebuildResult(BackfillOutcome Outcome, int Embedded, int Failed, int Remaining);

/// <summary>
/// <c>embed --rebuild</c>: throw the vector index away and make it again from <c>fact</c>.
/// </summary>
/// <remarks>
/// <para><b>Destroys nothing authored.</b> Every row here is derived from a fact body and an
/// embedder (D8), which is why this needs no snapshot while a migration does — a migration
/// rewrites structure that cannot be recomputed, and this recomputes by definition. The cost is
/// entirely in embedder calls, which is why the dry run states the count.</para>
///
/// <para><b>Which half runs is not the user's choice.</b> A same-space rebuild only needs the
/// rows gone; a width change invalidates the table, whose DDL carries the width; and a same-width
/// model swap invalidates it too, in the one way nothing downstream can detect — vec0 rejects a
/// wrong width at the row level but has no opinion about a vector of the right size from the
/// wrong model, which is the silent failure D18 names. So the plan reads what the index pinned
/// and picks, and the flag only decides whether to proceed.</para>
/// </remarks>
public static class VectorRebuild
{
    /// <summary>Reads what a rebuild would do. Writes nothing, so a dry run can call it.</summary>
    public static RebuildPlan Plan(SqliteConnection connection, EmbeddingSpace target)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Counted before anything is decided, because both numbers come from the table this is
        // about to change and neither survives the change.
        var embeddable = VectorIndex.CountEmbeddable(connection);

        if (!VectorIndex.Exists(connection))
        {
            return new RebuildPlan(
                RebuildAction.Build,
                Current: null,
                CurrentInput: null,
                target,
                VectorIndex.InputVersion,
                Discarded: 0,
                ToEmbed: embeddable);
        }

        var current = VectorIndex.ReadSpace(connection);
        var currentInput = VectorIndex.ReadInputVersion(connection);

        var keepable = current == target
            && string.Equals(currentInput, VectorIndex.InputVersion, StringComparison.Ordinal);

        return new RebuildPlan(
            keepable ? RebuildAction.Clear : RebuildAction.Recreate,
            current,
            currentInput,
            target,
            VectorIndex.InputVersion,
            Discarded: VectorIndex.Count(connection),
            ToEmbed: embeddable);
    }

    /// <summary>
    /// Carries out <paramref name="plan"/>: wipes, then embeds every live fact.
    /// </summary>
    /// <remarks>
    /// The refill is <see cref="VectorBackfill"/> one batch at a time rather than a single call
    /// with an unlimited budget, purely so a caller can report progress on a store where this
    /// takes minutes. Each pass re-checks the space and reconciles, both idempotent, so the loop
    /// costs nothing beyond the calls it was always going to make.
    ///
    /// <para><paramref name="progress"/> is a plain callback rather than an
    /// <see cref="IProgress{T}"/> because the only implementation anyone reaches for,
    /// <see cref="Progress{T}"/>, posts to the thread pool when there is no synchronisation
    /// context — which is every context this runs in. A CLI would print its batch lines
    /// interleaved or after its own summary.</para>
    /// </remarks>
    public static async Task<RebuildResult> RunAsync(
        SqliteConnection connection,
        IEmbedder embedder,
        RebuildPlan plan,
        int batchSize = VectorBackfill.DefaultBatchSize,
        Action<BackfillResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(plan);

        switch (plan.Action)
        {
            case RebuildAction.Clear:
                using (var transaction = EngramDatabase.BeginWrite(connection))
                {
                    VectorIndex.Clear(connection, transaction);
                    transaction.Commit();
                }

                break;

            case RebuildAction.Recreate:
                // Drop rather than clear, and drop the pins with it. Backfill's first act is
                // EnsureCreated, which is a no-op against a table that still exists — leaving it
                // would rebuild the new model's vectors into the old model's table and then let
                // the space check reject every pass afterwards.
                VectorIndex.Drop(connection);
                break;

            default:
                break;
        }

        var embedded = 0;
        var failed = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pass = await VectorBackfill.RunAsync(
                connection,
                embedder,
                batchSize,
                maxBatches: 1,
                cancellationToken).ConfigureAwait(false);

            embedded += pass.Embedded;
            failed += pass.Failed;
            progress?.Invoke(pass with { Embedded = embedded, Failed = failed });

            if (pass.Outcome is not BackfillOutcome.BatchLimitReached)
            {
                // Completed, stalled on a poison batch, or — despite the wipe above — mismatched.
                // All three are terminal: another pass would do the same thing again.
                return new RebuildResult(pass.Outcome, embedded, failed, pass.Remaining);
            }
        }
    }
}
