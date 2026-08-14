namespace Engram.Core;

/// <summary>
/// The background freshness loop (spec §6): polls <c>repo_enrollment</c> for what is due and
/// freshens one repo per tick, for as long as the server is up.
/// </summary>
/// <remarks>
/// <para><b>Why a poll, and why <c>repo_enrollment</c> rather than the spool queue.</b> The queue
/// only ever sees edits a <c>PostToolUse</c> hook observed, which is exactly what D67 was written
/// against — a <c>git pull</c>, a rebase, or a branch switch made outside any tool call never
/// enters it. Freshness is a time question and <c>last_full_scan_at</c> is the time record, so this
/// loop asks <see cref="RepoFreshness.NextDue"/> — the same selection policy items 2 and 3 use —
/// rather than growing a private notion of "due".</para>
///
/// <para><b>One repo per tick, same reasoning as the session-start self-heal.</b> A tick that
/// services every due repo is an unbounded pass wearing a bounded costume; one per tick makes
/// worst-case work per unit time constant, and <c>RepoFreshness</c>'s most-neglected-first ordering
/// makes it converge across repos rather than starving one.</para>
///
/// <para><b>Ambient, never commanded.</b> Nothing is waiting on this loop — a newly enrolled repo
/// already gets its own spawn and a session that touches a repo already gets item 3 — so lock
/// contention (§6.4) is handled the same way <c>index --freshen</c> handles it: silently, moving on
/// to whatever the next tick finds due.</para>
/// </remarks>
public sealed class IndexFreshness(EngramHome home, Action<string>? log = null)
{
    /// <summary>
    /// How often the loop asks what is due. Not a second freshness policy — the policy decides
    /// work, this only decides how often it is asked. Chosen for the largest interval that keeps
    /// tail latency tolerable rather than the smallest one that feels responsive, since nothing is
    /// waiting on this loop (spec §6.2).
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlySet<string> NoExclusions = new HashSet<string>(StringComparer.Ordinal);

    private DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        log?.Invoke("Index freshness service started.");

        startedAt = DateTimeOffset.UtcNow;
        Publish(repo: null, outcome: "idle", error: null);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TickOnce();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A tick that throws must not take the server with it — the next due repo is
                // still there next tick, same as a failed embedding pass leaves its queue intact.
                log?.Invoke($"Index freshness tick failed: {ex.Message}");
            }

            if (!await DelayAsync(PollInterval, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        log?.Invoke("Index freshness service stopped.");

        // Removed rather than stamped "stopped", same reasoning as EmbeddingProgress: a note left
        // behind carries a timestamp that was true when written, and absent is unambiguous.
        IndexProgress.Clear(home);
    }

    /// <summary>
    /// One tick: selects at most one due repo and freshens it. Opens its own connection and config
    /// read, per tick rather than held open, so a <c>repair</c> that replaces the database file is
    /// not fighting a connection this loop has kept since startup.
    /// </summary>
    public void TickOnce()
    {
        using var connection = EngramDatabase.OpenInitialized(home);
        var config = ConfigFile.Load(home.ConfigPath);
        var settings = IndexingSettings.Read(config);
        var now = DateTimeOffset.UtcNow;

        var candidate = RepoFreshness.NextDue(
            connection,
            IndexingSettings.FullScanIntervalMinutes,
            now,
            includeAmbient: true,
            NoExclusions);

        if (candidate is null)
        {
            Publish(repo: null, outcome: "idle", error: null);
            return;
        }

        var identity = candidate.Row.Identity;
        IndexTelemetry.Note(home, "server", "started", identity);
        Publish(identity, outcome: "running", error: null);

        try
        {
            var report = RepoIndexRun.Freshen(
                connection, home, config, settings, candidate.Root, apply: true, budget: null, now);

            // §6.4: ambient — stay silent on lock contention, same as `index --freshen`; nobody is
            // watching for the note and the next tick picks another candidate.
            var lockNote = report.Notes.FirstOrDefault(
                n => n.StartsWith("skipped: another process is indexing this repo", StringComparison.Ordinal));
            if (lockNote is null)
            {
                IndexTelemetry.Note(home, "server", "finished", identity);
                log?.Invoke($"Freshened {identity}.");
            }

            Publish(identity, outcome: "idle", error: null);
        }
        catch (Exception ex)
        {
            IndexTelemetry.Note(home, "server", "failed", identity);
            Publish(identity, outcome: "failed", ex.Message);
            throw;
        }
    }

    private void Publish(string? repo, string outcome, string? error) =>
        IndexProgress.Write(
            home,
            new IndexProgress(
                DateTimeOffset.UtcNow,
                startedAt,
                Environment.ProcessId,
                ProcessStartToken.ForSelf(),
                repo,
                outcome,
                error));

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
