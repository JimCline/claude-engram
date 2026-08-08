namespace Engram.Core;

/// <summary>
/// The one place vectors are produced: a loop that drains the backfill queue for as long as the
/// server is up.
/// </summary>
/// <remarks>
/// <para><b>Why one, and why here.</b> A model is resident and expensive — a second process
/// loading one costs its memory again for no more throughput, and two processes embedding the
/// same queue would race to write the same rows. Engram already runs exactly one long-lived
/// process per home, guarded by its pid file, so the singular embedder is not a new daemon with
/// a new lock: it is a loop inside the one that already exists. A hook or a second CLI
/// invocation writes facts and walks away; this is what turns them into vectors.</para>
///
/// <para><b>The contract is eventual, and deliberately so.</b> <c>remember</c> returns as soon
/// as the fact is durable. It never waits on an embedding, because the write path may not
/// depend on a model being loaded, reachable, or correct (D4, D18) — a fact stated to an agent
/// must land even with the endpoint down. So a new fact is lexically searchable immediately and
/// becomes semantically searchable when this loop next runs. The queue is a query over
/// <c>fact</c>, so nothing is lost in between: a crash, a restart, or a week with embeddings
/// switched off leaves exactly the same work to do, and no bookkeeping to reconcile.</para>
///
/// <para><b>Idle is the common case.</b> A poll that costs a COUNT over a LEFT JOIN is cheap,
/// but not free forever, so the interval backs off when there is nothing to do and snaps back
/// the moment there is. That keeps a fact written during a busy session waiting seconds rather
/// than a fixed worst case, without a wakeup a minute on a machine nobody is using.</para>
/// </remarks>
public sealed class EmbeddingBacklog(
    EngramHome home,
    IEmbedder embedder,
    EmbeddingSettings settings,
    Action<string>? log = null)
{
    public static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait before the next pass, given what the last one did.
    /// </summary>
    /// <remarks>
    /// A stalled or mismatched pass waits the idle interval rather than retrying immediately:
    /// both mean the next pass would do the identical thing and fail the identical way, and
    /// hammering a broken endpoint every two seconds is how a local runtime gets blamed for
    /// load Engram generated.
    /// </remarks>
    public static TimeSpan NextDelay(BackfillResult result) => result.Outcome switch
    {
        BackfillOutcome.BatchLimitReached => BusyInterval,
        BackfillOutcome.Completed when result.Embedded > 0 => BusyInterval,
        _ => IdleInterval,
    };

    private readonly List<string> recent = [];
    private DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private int sessionEmbedded;
    private int sessionFailed;

    /// <summary>One pass over the queue. Opens its own connection and closes it.</summary>
    /// <remarks>
    /// Per pass rather than held open, so a <c>repair</c> that replaces the database file is not
    /// fighting a connection this loop has kept since startup. The open costs 1.0–1.5 ms against
    /// an interval measured in seconds.
    /// </remarks>
    public async Task<BackfillResult> DrainOnceAsync(CancellationToken cancellationToken)
    {
        using var connection = EngramDatabase.Open(home);

        return await VectorBackfill.RunAsync(
            connection,
            embedder,
            settings.MaxBatch,
            maxBatches: 8,
            cancellationToken,
            onBatchWritten: NoteBatch).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        log?.Invoke($"Embedding backlog started for {embedder.Space}.");

        startedAt = DateTimeOffset.UtcNow;
        Publish(outcome: "starting", error: null);

        var lastReported = default(BackfillOutcome?);

        while (!cancellationToken.IsCancellationRequested)
        {
            BackfillResult result;
            try
            {
                result = await DrainOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A pass that throws must not take the server with it. The queue is durable, so
                // the work is still there; what matters is that the reason is said once rather
                // than every two seconds.
                log?.Invoke($"Embedding pass failed: {ex.Message}");
                Publish(outcome: "failed", error: ex.Message);
                await DelayAsync(IdleInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Embedded is counted in NoteBatch, which fires once per committed batch and therefore
            // sums to exactly this pass's total. Adding it again here would double it.
            sessionFailed += result.Failed;

            if (result.Embedded > 0)
            {
                log?.Invoke(
                    $"Embedded {result.Embedded} fact(s); {result.Remaining} pending.");
            }
            else if (result.Outcome != BackfillOutcome.Completed && result.Outcome != lastReported)
            {
                // Said once per change of state, not once per pass — a mismatched index would
                // otherwise write a line every thirty seconds for as long as it stayed wrong.
                log?.Invoke($"Embedding backlog {result.Outcome}: {result.Remaining} pending.");
            }

            // Every pass, including the idle ones that log nothing. The timestamp is what tells a
            // reader the loop is alive, so a quiet loop that stops writing it is indistinguishable
            // from a dead one — which is the whole point of recording it.
            Publish(result.Outcome.ToString(), error: null);

            lastReported = result.Outcome;

            if (!await DelayAsync(NextDelay(result), cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        log?.Invoke("Embedding backlog stopped.");

        // Removed rather than stamped "stopped": a note left behind would carry a timestamp that
        // was true when written, and anything reading it later has to decide whether to believe
        // it. Absent is unambiguous, and the store still answers how much work is outstanding.
        EmbeddingProgress.Clear(home);
    }

    private void NoteBatch(IReadOnlyList<string> bodies)
    {
        foreach (var body in bodies)
        {
            recent.Insert(0, EmbeddingProgress.Summarize(body));
        }

        if (recent.Count > EmbeddingProgress.RecentKept)
        {
            recent.RemoveRange(EmbeddingProgress.RecentKept, recent.Count - EmbeddingProgress.RecentKept);
        }

        sessionEmbedded += bodies.Count;

        // Published per batch, not per pass: a pass is up to eight batches and can run half a
        // minute, which is long enough for a watcher to conclude nothing is happening.
        Publish(outcome: "running", error: null);
    }

    private void Publish(string? outcome, string? error) =>
        EmbeddingProgress.Write(
            home,
            new EmbeddingProgress(
                DateTimeOffset.UtcNow,
                startedAt,
                Environment.ProcessId,
                embedder.Space.ToString(),
                sessionEmbedded,
                sessionFailed,
                outcome,
                error,
                [.. recent]));

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
