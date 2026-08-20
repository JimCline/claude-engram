using Engram.Core;

namespace Engram.Integration.Tests;

// D-5, from the read side PrimerSummary.Read builds: a directive is delivered through its own
// undroppable block (PrimerSummary.Directives), never counted as an ordinary fact — folding it
// into FactCount or TopicCounts would double-report it once PrimerBuilder renders both blocks.
public class DirectivePrimerSummaryExclusionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADirective_IsExcludedFromFactCountAndTopicCounts_ButAppearsInDirectives()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        FactStore.Remember(
            connection,
            new FactWrite("/facts/tabs", "note", "requires", "always use spaces", "user", "stated"),
            T0);

        var before = PrimerSummary.Read(connection, T0.AddSeconds(1));

        DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0.AddSeconds(2));

        var after = PrimerSummary.Read(connection, T0.AddSeconds(3));

        Assert.Equal(before.FactCount, after.FactCount);
        Assert.Equal(before.TopicCounts, after.TopicCounts);
        Assert.Equal(["always use BEGIN IMMEDIATE for writes"], after.Directives);
    }
}
