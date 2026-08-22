using Engram.Core;

namespace Engram.Integration.Tests;

// A directive is an ordinary fact row (D-3), so it rides FactJournal.Write/Replay unchanged —
// this proves that additive-replay path actually carries a directive across a rebuild.
public class DirectiveBackupReplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BackupReplay_RoundTripsADirective_IntoAStoreThatWasEmpty()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0);
            FactJournal.Write(connection, source.Home, T0.AddMinutes(1));
        }

        var facts = FactJournal.Parse(File.ReadLines(FactJournal.PathIn(source.Home)), out var skipped);
        Assert.Equal(0, skipped);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.AlreadyPresent);

        var replayed = Assert.Single(DirectiveFacts.ReadLive(rebuilt));
        Assert.Equal("always use BEGIN IMMEDIATE for writes", replayed.Body);
    }
}
