using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 (D9). <c>fact_relation</c>'s CHECK constraint on <c>relation</c> needs a real,
/// schema-applied connection to enforce at all — a unit test over a fake would only prove the
/// fake agrees with itself — so it sits here beside the rest of this table's behaviour rather
/// than in <c>Engram.Core.Tests</c>.
/// </summary>
public class FactRelationsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Insert_WithARelationOutsideTheCheckConstraint_Throws()
    {
        using var fixture = new RelationFixture();
        var (a, b) = fixture.TwoFacts();

        using var transaction = EngramDatabase.BeginWrite(fixture.Connection);
        Assert.Throws<SqliteException>(() =>
            FactRelations.Insert(fixture.Connection, transaction, a, b, "duplicates", null, T0.ToUnixTimeSeconds()));
    }

    [Fact]
    public void Judge_WithAnUnrecognizedRelation_ThrowsBeforeTouchingTheStore()
    {
        using var fixture = new RelationFixture();
        var (a, b) = fixture.TwoFacts();

        using var transaction = EngramDatabase.BeginWrite(fixture.Connection);
        Assert.Throws<ArgumentException>(() =>
            FactRelations.Judge(fixture.Connection, transaction, a, b, "duplicates", null, T0.ToUnixTimeSeconds()));

        Assert.Empty(FactRelations.ForFact(fixture.Connection, a));
    }

    [Fact]
    public void Judge_AFactAgainstItself_ThrowsBeforeTouchingTheStore()
    {
        using var fixture = new RelationFixture();
        var (a, _) = fixture.TwoFacts();

        using var transaction = EngramDatabase.BeginWrite(fixture.Connection);
        Assert.Throws<ArgumentException>(() =>
            FactRelations.Judge(fixture.Connection, transaction, a, a, "conflicts_with", null, T0.ToUnixTimeSeconds()));

        Assert.Empty(FactRelations.ForFact(fixture.Connection, a));
    }

    [Fact]
    public void Judge_WritesOneRowVisibleFromEitherFactsHistory()
    {
        using var fixture = new RelationFixture();
        var (a, b) = fixture.TwoFacts();

        using (var transaction = EngramDatabase.BeginWrite(fixture.Connection))
        {
            FactRelations.Judge(
                fixture.Connection, transaction, a, b, "conflicts_with", "same slot, disagreeing bodies", T0.ToUnixTimeSeconds());
            transaction.Commit();
        }

        var fromA = Assert.Single(FactRelations.ForFact(fixture.Connection, a));
        var fromB = Assert.Single(FactRelations.ForFact(fixture.Connection, b));
        Assert.Equal(fromA.Id, fromB.Id);
        Assert.Equal("conflicts_with", fromA.Relation);
        Assert.Equal("same slot, disagreeing bodies", fromA.Reason);
    }

    [Fact]
    public void RelationCounts_CountsAFactOnEitherSide()
    {
        using var fixture = new RelationFixture();
        var (a, b) = fixture.TwoFacts();
        var c = fixture.AnotherFact();

        using (var transaction = EngramDatabase.BeginWrite(fixture.Connection))
        {
            FactRelations.Judge(fixture.Connection, transaction, a, b, "not_conflict", "different scopes", T0.ToUnixTimeSeconds());
            FactRelations.Judge(fixture.Connection, transaction, c, a, "supersedes", "restated", T0.ToUnixTimeSeconds() + 1);
            transaction.Commit();
        }

        var counts = FactRelations.RelationCounts(fixture.Connection);
        Assert.Equal(2, counts[a]);
        Assert.Equal(1, counts[b]);
        Assert.Equal(1, counts[c]);
    }

    private sealed class RelationFixture : IDisposable
    {
        private readonly SandboxHome sandbox = new(initialize: false);
        private int nextSubject;

        public RelationFixture()
        {
            Connection = EngramDatabase.OpenInitialized(sandbox.Home);
        }

        public SqliteConnection Connection { get; }

        public (long A, long B) TwoFacts() => (OneFact(), OneFact());

        public long AnotherFact() => OneFact();

        private long OneFact()
        {
            nextSubject++;
            var result = FactStore.Remember(
                Connection,
                new FactWrite($"/people/jim-{nextSubject}", "person", "prefers", "Tabs over spaces.", "user", "stated"),
                T0,
                FactStore.DefaultSupersessionReason);
            return result.FactId;
        }

        public void Dispose()
        {
            Connection.Dispose();
            sandbox.Dispose();
        }
    }
}
