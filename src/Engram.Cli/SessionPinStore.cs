using System.Collections.Concurrent;

namespace Engram.Cli;

/// <summary>
/// Per-session pin (docs/memory-expansion/04-lifecycle-spec.md). No database row: pin state does
/// not need to survive anything, so it lives entirely in server memory, keyed by
/// <see cref="McpSessionId"/>. D8 is satisfied trivially — nothing is persisted.
/// </summary>
public sealed class SessionPinStore
{
    private static readonly IReadOnlySet<long> EmptyPins = new HashSet<long>();

    private readonly ConcurrentDictionary<McpSessionId, HashSet<long>> pinsBySession = new();

    /// <summary>Pins a fact for this session. Returns whether it was newly added.</summary>
    public bool Pin(McpSessionId session, long factId)
    {
        var pins = pinsBySession.GetOrAdd(session, _ => []);
        lock (pins)
        {
            return pins.Add(factId);
        }
    }

    /// <summary>Unpins a fact for this session. Returns whether it had actually been pinned.</summary>
    public bool Unpin(McpSessionId session, long factId)
    {
        if (!pinsBySession.TryGetValue(session, out var pins))
        {
            return false;
        }

        lock (pins)
        {
            return pins.Remove(factId);
        }
    }

    /// <summary>Every fact currently pinned for this session.</summary>
    public IReadOnlySet<long> PinnedFor(McpSessionId session)
    {
        if (!pinsBySession.TryGetValue(session, out var pins))
        {
            return EmptyPins;
        }

        lock (pins)
        {
            return new HashSet<long>(pins);
        }
    }
}
