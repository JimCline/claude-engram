using Engram.Cli;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 for per-session pin (docs/memory-expansion/04-lifecycle-spec.md): two different
/// <see cref="McpSessionId"/>s never see each other's pins. Falsify: replace the per-session
/// dictionary with one global set, confirm <see cref="APinInOneSession_IsInvisibleToAnother"/>
/// starts failing.
/// </summary>
public class SessionPinStoreTests
{
    [Fact]
    public void APinInOneSession_IsInvisibleToAnother()
    {
        var store = new SessionPinStore();
        var sessionA = new McpSessionId("session-a");
        var sessionB = new McpSessionId("session-b");

        store.Pin(sessionA, 42);

        Assert.Contains(42L, store.PinnedFor(sessionA));
        Assert.DoesNotContain(42L, store.PinnedFor(sessionB));
    }

    [Fact]
    public void UnpinningInOneSession_DoesNotAffectAnothersIdenticalPin()
    {
        var store = new SessionPinStore();
        var sessionA = new McpSessionId("session-a");
        var sessionB = new McpSessionId("session-b");

        store.Pin(sessionA, 42);
        store.Pin(sessionB, 42);

        store.Unpin(sessionA, 42);

        Assert.DoesNotContain(42L, store.PinnedFor(sessionA));
        Assert.Contains(42L, store.PinnedFor(sessionB));
    }

    [Fact]
    public void ASessionWithNoPins_ReturnsAnEmptySet()
    {
        var store = new SessionPinStore();

        Assert.Empty(store.PinnedFor(new McpSessionId("session-unused")));
    }
}
