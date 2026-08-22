namespace Engram.Core;

/// <summary>
/// One known peer machine and the latest moment this machine has observed it, if ever
/// (docs/memory-expansion/01-sync-spec.md, "Staleness/liveness detection"). Gathered by
/// <see cref="Sync.GatherPeerObservations"/> from <c>sync_chunk_state.applied_at</c> and
/// filesystem mtimes under the peer's chunk directory — deliberately kept separate from
/// <see cref="SyncStaleness.Evaluate"/> so the staleness rule itself stays a pure function.
/// </summary>
public readonly record struct PeerObservation(string MachineId, DateTimeOffset? LastObservedUtc);

/// <summary>One peer's staleness verdict, as decided by <see cref="SyncStaleness.Evaluate"/>.</summary>
public readonly record struct PeerStaleness(string MachineId, DateTimeOffset? LastObservedUtc, bool IsStale);

/// <summary>
/// The staleness rule (docs/memory-expansion/01-sync-spec.md, "Staleness/liveness detection"): a
/// peer this machine has never observed is never stale — there is nothing to compare against, and
/// a freshly enrolled peer with no chunks yet must not read as gone quiet. Pure and Tier-1
/// testable, mirroring <see cref="SyncScope"/>'s no-I/O pattern.
/// </summary>
public static class SyncStaleness
{
    public static IReadOnlyList<PeerStaleness> Evaluate(
        IReadOnlyList<PeerObservation> peers, DateTimeOffset now, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(peers);

        var results = new List<PeerStaleness>(peers.Count);
        foreach (var peer in peers)
        {
            var isStale = peer.LastObservedUtc.HasValue && (now - peer.LastObservedUtc.Value) > staleAfter;
            results.Add(new PeerStaleness(peer.MachineId, peer.LastObservedUtc, isStale));
        }

        return results;
    }
}
