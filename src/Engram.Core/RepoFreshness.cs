using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// Why a repo is being offered for a full scan. The distinction is not cosmetic: an unfulfilled
/// user enrollment is the retry of a command already given and already announced, and is not
/// gated by <c>auto_index_on_session_start</c>; everything else is ambient upkeep and is gated
/// (D67, and §5.3 of the spec).
/// </summary>
public enum FreshnessReason
{
    /// <summary><c>last_full_scan_at IS NULL</c> and <c>source = 'user'</c>: the user typed
    /// <c>engram repo enroll</c> (or called the MCP tool), Engram printed "The first index is
    /// running in the background", and that index demonstrably never completed.</summary>
    UnfulfilledEnrollment,

    /// <summary><c>last_full_scan_at IS NULL</c> and <c>source = 'backfill'</c>: the v6-&gt;v7
    /// migration inferred this enrollment. Nobody asked for it and nobody was told anything, so
    /// it is ambient.</summary>
    NeverScanned,

    /// <summary><c>last_full_scan_at</c> is set but older than the interval that applied.</summary>
    Stale,
}

public sealed record FreshnessCandidate(RepoEnrollmentRow Row, string Root, FreshnessReason Reason);

/// <summary>
/// One selection policy for turning enrolled repos into freshness work, shared by every caller
/// that would otherwise need to re-derive it: <c>doctor</c>, <c>engram repo index --all</c>, the
/// session-start self-heal, and the background freshness service.
/// </summary>
public static class RepoFreshness
{
    /// <summary>
    /// How long doctor waits after a decision before calling a NULL scan stamp neglect rather
    /// than work still in flight. Its basis is NE-3, not taste: it must exceed the wall time of
    /// one full applied index of the largest enrolled repo by an order of magnitude. Ships at one
    /// hour only if NE-3 measures that run at or under six minutes — NE-3 has not been run yet,
    /// so this value is provisional until it is.
    /// </summary>
    public static readonly TimeSpan EnrollmentGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// How long doctor waits before calling a stamped repo neglected. Deliberately far longer
    /// than <see cref="IndexingSettings.FullScanIntervalMinutes"/>: "due" drives work, "neglected"
    /// drives a warning, and warning at 61 minutes is how people learn to stop reading doctor
    /// (D37). Seven days is chosen so that neglect implies a broken mechanism rather than a lull —
    /// item 3 heals one repo per session start, so a week is dozens to hundreds of chances.
    /// </summary>
    /// <remarks>
    /// This is numerically equal to <see cref="RepoEnrollment.DeferralCooldown"/> and MUST NOT be
    /// replaced by it or by a shared constant. That one is a consent interval — how long before
    /// re-asking a human who said "not now" — and moves with how irritating re-prompting is. This
    /// one is a diagnostic threshold and moves with the heal cadence. No test can hold them apart
    /// while they are equal, which is why this comment is the guard.
    /// </remarks>
    public static readonly TimeSpan NeglectedAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// Every enrolled repo whose checkout is present on disk and whose full scan is due,
    /// most-neglected first: NULL stamps before stamped ones, oldest <c>decided_at</c> within the
    /// NULLs, oldest <c>last_full_scan_at</c> within the rest, identity as a total-order tiebreak.
    /// Ordering is what makes a bounded caller (<see cref="NextDue"/>) converge across repos
    /// instead of starving one forever. Read-only. Does no filesystem work beyond
    /// <see cref="Directory.Exists(string?)"/> per row.
    /// </summary>
    public static IReadOnlyList<FreshnessCandidate> Due(
        SqliteConnection connection, int intervalMinutes, DateTimeOffset now, IReadOnlySet<string> exclude)
    {
        return RepoEnrollment.ListAll(connection)
            .Where(row => IsSelectable(row, exclude))
            .Where(row => RepoEnrollment.IsFullScanDue(row, intervalMinutes, now))
            .Select(ToDueCandidate)
            .OrderBy(candidate => candidate, DueOrder)
            .ToList();
    }

    /// <summary>
    /// Bounded selection for the session-start child and the background service.
    /// <paramref name="includeAmbient"/> false restricts the result to
    /// <see cref="FreshnessReason.UnfulfilledEnrollment"/>. Returns at most one candidate. Never
    /// returns a root in <paramref name="exclude"/> (canonicalized through
    /// <see cref="PathCanonicalizer.Canonical"/>).
    /// </summary>
    public static FreshnessCandidate? NextDue(
        SqliteConnection connection, int intervalMinutes, DateTimeOffset now,
        bool includeAmbient, IReadOnlySet<string> exclude)
    {
        // Filtering the already-ordered Due() result rather than re-querying preserves the same
        // most-neglected-first order within the ambient-restricted subset — a second, separately
        // maintained ordering here could silently diverge from Due()'s.
        return Due(connection, intervalMinutes, now, exclude)
            .Where(candidate => includeAmbient || candidate.Reason == FreshnessReason.UnfulfilledEnrollment)
            .FirstOrDefault();
    }

    /// <summary>
    /// Rows doctor should warn about. Not the same predicate as <see cref="Due"/>: that one uses
    /// <see cref="IndexingSettings.FullScanIntervalMinutes"/>, and warning about every repo not
    /// scanned in the last hour would leave doctor amber essentially always (D37). This predicate
    /// uses <see cref="EnrollmentGrace"/> for a never-scanned row and <see cref="NeglectedAfter"/>
    /// for a stamped one — both far longer, because neglect is meant to imply a broken mechanism,
    /// not an ordinary lull.
    /// </summary>
    public static IReadOnlyList<FreshnessCandidate> Neglected(SqliteConnection connection, DateTimeOffset now)
    {
        var noExclusions = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

        return RepoEnrollment.ListAll(connection)
            .Where(row => IsSelectable(row, noExclusions))
            .Select(row => (Row: row, Reason: ClassifyNeglect(row, now)))
            .Where(candidate => candidate.Reason is not null)
            .Select(candidate => new FreshnessCandidate(candidate.Row, candidate.Row.LastRoot!, candidate.Reason!.Value))
            .OrderBy(candidate => candidate.Row.Identity, StringComparer.Ordinal)
            .ToList();
    }

    // Shared by Due, NextDue (through Due) and Neglected — mirrors the filter already at
    // IndexCommand.cs's DrainOtherEnrolledRoots so the two agree. An enrolled repo whose checkout
    // is absent is deliberately not a candidate: a missing checkout is not a freshness problem.
    private static bool IsSelectable(RepoEnrollmentRow row, IReadOnlySet<string> exclude) =>
        row.State == RepoEnrollmentState.Enrolled
        && row.LastRoot is { } root
        && Directory.Exists(root)
        && (exclude.Count == 0 || !exclude.Contains(PathCanonicalizer.Canonical(root)));

    private static FreshnessCandidate ToDueCandidate(RepoEnrollmentRow row) =>
        new(row, row.LastRoot!, ClassifyDueReason(row));

    // A row only reaches here already filtered to IsFullScanDue == true, so a stamped row is
    // stale by definition; only the NULL case needs to distinguish an unfulfilled ask from
    // ambient backfill.
    internal static FreshnessReason ClassifyDueReason(RepoEnrollmentRow row) =>
        row.LastFullScanAt is not null
            ? FreshnessReason.Stale
            : row.Source == "user"
                ? FreshnessReason.UnfulfilledEnrollment
                : FreshnessReason.NeverScanned;

    private static FreshnessReason? ClassifyNeglect(RepoEnrollmentRow row, DateTimeOffset now)
    {
        if (row.LastFullScanAt is { } scan)
        {
            return now.ToUnixTimeSeconds() - scan > NeglectedAfter.TotalSeconds ? FreshnessReason.Stale : null;
        }

        if (now.ToUnixTimeSeconds() - row.DecidedAt > EnrollmentGrace.TotalSeconds)
        {
            return row.Source == "user" ? FreshnessReason.UnfulfilledEnrollment : FreshnessReason.NeverScanned;
        }

        return null;
    }

    // Most-neglected first: NULL stamps sort before stamped ones; within each group, oldest
    // first; identity is the total-order tiebreak so two rows with equal timestamps still compare
    // deterministically (needed for NextDue to converge across repos rather than favoring
    // whichever the DB happens to return first on a tie).
    internal static readonly IComparer<FreshnessCandidate> DueOrder = Comparer<FreshnessCandidate>.Create(
        (a, b) =>
        {
            var aStamped = a.Row.LastFullScanAt is not null;
            var bStamped = b.Row.LastFullScanAt is not null;
            if (aStamped != bStamped)
            {
                return aStamped ? 1 : -1;
            }

            var byRecency = aStamped
                ? a.Row.LastFullScanAt!.Value.CompareTo(b.Row.LastFullScanAt!.Value)
                : a.Row.DecidedAt.CompareTo(b.Row.DecidedAt);

            return byRecency != 0 ? byRecency : string.CompareOrdinal(a.Row.Identity, b.Row.Identity);
        });
}
