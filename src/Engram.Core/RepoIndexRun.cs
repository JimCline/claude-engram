using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The only place a <see cref="FreshnessCandidate"/> becomes an index run — shared by every
/// mechanism that acts on <see cref="RepoFreshness"/>'s selection so none of them grows a private
/// copy of how a freshening scan is invoked.
/// </summary>
public static class RepoIndexRun
{
    /// <summary>
    /// A full scan of one enrolled repo, with no spool-queue interaction at all.
    /// </summary>
    /// <remarks>
    /// <c>Drain: false</c> is what forces the full scan (<c>CodeIndexer.cs:110</c>), so this needs
    /// no <c>Full: true</c> — and must not pass one: an explicit <c>--full</c> would permanently
    /// disarm the falsification that a NULL <c>last_full_scan_at</c> is what makes the first scan
    /// full. <c>AllowFullScanDue</c> is left at its <c>true</c> default and is irrelevant here for
    /// the same reason — <c>Drain: false</c> already forces the full scan regardless of that flag.
    ///
    /// Not draining is deliberate and is what keeps this off the drain path's losslessness
    /// argument: <c>DiscardExcept</c> is that path's bound, and a caller that consumed from the
    /// queue without being part of the three-step drain pass could discard an entry no root
    /// scanned for.
    /// </remarks>
    public static IndexReport Freshen(
        SqliteConnection connection,
        EngramHome home,
        ConfigFile config,
        IndexingSettings settings,
        string root,
        string identity,
        bool apply,
        ScanBudget? budget,
        DateTimeOffset now)
        => CodeIndexer.Index(
            connection,
            home,
            config,
            settings,
            new IndexOptions(root, apply, Drain: false, Full: false, Budget: budget, EnrolledIdentity: identity),
            now);
}
