namespace Engram.Core;

/// <summary>
/// Which bucket <c>sync compact</c> puts one previously-exported identity into
/// (docs/memory-expansion/01-sync-spec.md, "Chunk retention/pruning").
/// </summary>
public enum SyncRetentionBucket
{
    /// <summary>Still open — kept unconditionally, however old the original export was.</summary>
    Live,

    /// <summary>Closed, but within <c>[sync] retain_days</c> of now — kept.</summary>
    RetainedClosed,

    /// <summary>Closed longer ago than <c>retain_days</c> — dropped from the consolidated chunk.</summary>
    Dropped,
}

/// <summary>
/// The bucket-partition rule <c>sync compact</c> runs its own chunk history through
/// (docs/memory-expansion/01-sync-spec.md, "Chunk retention/pruning — decided"): live facts are
/// always kept, closed facts are kept only if closed within <c>retain_days</c>. Pure and Tier-1
/// testable over fabricated identity sets, deliberately kept separate from the file I/O in
/// <see cref="Sync.Compact"/>.
/// </summary>
public static class SyncCompaction
{
    /// <param name="allExported">Every identity this machine has ever exported in its own chunk history.</param>
    /// <param name="openAtExport">
    /// Identities whose own fact record was still open (no <c>valid_to</c>) when exported — see
    /// <see cref="FactIdentity"/>. An identity in <paramref name="allExported"/> but absent here was
    /// already closed at the moment it was first exported (no separate close record was ever
    /// emitted for it), which is why membership here alone does not decide the bucket: it is closed
    /// either by being missing from this set, or by having a <paramref name="closedAt"/> entry.
    /// </param>
    /// <param name="closedAt">
    /// The closure timestamp for every identity known to be closed — from a separate <c>"t":"close"</c>
    /// record's <c>ValidTo</c> when one exists, or from the identity's own already-closed fact
    /// record's <c>ValidTo</c> otherwise (see <see cref="Sync.Compact"/>). An identity absent from
    /// both this map and <paramref name="openAtExport"/> is retained conservatively rather than
    /// dropped, since a closed identity with no known age cannot be safely aged out.
    /// </param>
    public static IReadOnlyDictionary<FactIdentity, SyncRetentionBucket> Partition(
        IReadOnlyCollection<FactIdentity> allExported,
        IReadOnlyCollection<FactIdentity> openAtExport,
        IReadOnlyDictionary<FactIdentity, DateTimeOffset> closedAt,
        DateTimeOffset now,
        TimeSpan retain)
    {
        ArgumentNullException.ThrowIfNull(allExported);
        ArgumentNullException.ThrowIfNull(openAtExport);
        ArgumentNullException.ThrowIfNull(closedAt);

        var openSet = openAtExport as HashSet<FactIdentity> ?? new HashSet<FactIdentity>(openAtExport);
        var result = new Dictionary<FactIdentity, SyncRetentionBucket>();

        foreach (var identity in allExported)
        {
            var isClosed = !openSet.Contains(identity) || closedAt.ContainsKey(identity);
            if (!isClosed)
            {
                result[identity] = SyncRetentionBucket.Live;
                continue;
            }

            if (!closedAt.TryGetValue(identity, out var at))
            {
                result[identity] = SyncRetentionBucket.RetainedClosed;
                continue;
            }

            result[identity] = (now - at) > retain ? SyncRetentionBucket.Dropped : SyncRetentionBucket.RetainedClosed;
        }

        return result;
    }
}
