using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 (D9). The temporal model is the thing this system can most easily get subtly
/// wrong — a write that overwrites instead of superseding loses history silently, and no
/// unit test over an in-memory list would notice.
/// </summary>
public class FactStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Remember_WritesALiveFact()
    {
        using var fixture = new StoreFixture();

        var result = fixture.Remember("prefers", "Tabs over spaces.");

        var live = FactStore.ReadLive(fixture.Connection);
        var fact = Assert.Single(live);
        Assert.Equal(result.FactId, fact.Id);
        Assert.Equal("Tabs over spaces.", fact.Body);
        Assert.Null(fact.ValidTo);
        Assert.Null(result.SupersededFactId);
    }

    [Fact]
    public void Remember_ReusesTheSubjectEntityRatherThanDuplicatingIt()
    {
        using var fixture = new StoreFixture();

        fixture.Remember("prefers", "Tabs.");
        fixture.Remember("uses", "dotnet 10.");

        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM entity;"));
    }

    [Fact]
    public void Remember_OnALivePredicate_ClosesTheOldFactInsteadOfOverwritingIt()
    {
        using var fixture = new StoreFixture();

        var first = fixture.Remember("prefers", "Tabs.");
        var second = fixture.Remember("prefers", "Spaces.", T0.AddHours(1));

        Assert.Equal(first.FactId, second.SupersededFactId);

        var live = Assert.Single(FactStore.ReadLive(fixture.Connection));
        Assert.Equal("Spaces.", live.Body);

        // The old belief is still on disk, closed and pointing at what replaced it.
        var history = FactStore.History(fixture.Connection, StoreFixture.SubjectPath, "prefers");
        Assert.Equal(2, history.Count);
        Assert.Equal("Tabs.", history[0].Body);
        Assert.Equal(T0.AddHours(1).ToUnixTimeSeconds(), history[0].ValidTo);
        Assert.Equal(second.FactId, history[0].SupersededBy);
    }

    [Fact]
    public void Remember_RecordsWhyTheBeliefChanged()
    {
        using var fixture = new StoreFixture();

        var first = fixture.Remember("prefers", "Tabs.");
        var second = fixture.Remember("prefers", "Spaces.", T0.AddHours(1), reason: "the user changed their mind");

        using var command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT old_fact_id, new_fact_id, reason FROM supersession;";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(first.FactId, reader.GetInt64(0));
        Assert.Equal(second.FactId, reader.GetInt64(1));
        Assert.Equal("the user changed their mind", reader.GetString(2));
        Assert.False(reader.Read());
    }

    // The reason Remember has to close before it inserts. Without this constraint the
    // ordering would be a stylistic choice; with it, the other order cannot commit.
    [Fact]
    public void TwoLiveFactsOnOneSubjectAndPredicate_AreRefusedByTheDatabase()
    {
        using var fixture = new StoreFixture();
        fixture.Remember("prefers", "Tabs.");

        var subjectId = Convert.ToInt64(fixture.Scalar("SELECT id FROM entity;"));

        var error = Assert.Throws<SqliteException>(() => fixture.Execute(
            $"""
            INSERT INTO fact (subject_id, predicate, body, path, scope, learned_via, valid_from, created_at)
            VALUES ({subjectId}, 'prefers', 'Spaces.', '{StoreFixture.SubjectPath}', 'user', 'stated', 1, 1);
            """));

        Assert.Contains("UNIQUE", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Different predicates about the same subject are different beliefs and must not collide.
    [Fact]
    public void Remember_DifferentPredicatesOnOneSubject_BothStayLive()
    {
        using var fixture = new StoreFixture();

        fixture.Remember("prefers", "Tabs.");
        fixture.Remember("uses", "dotnet 10.");

        Assert.Equal(2, FactStore.ReadLive(fixture.Connection).Count);
    }

    [Fact]
    public void ReadAsOf_AnswersWithWhatWasBelievedThen()
    {
        using var fixture = new StoreFixture();

        fixture.Remember("prefers", "Tabs.");
        fixture.Remember("prefers", "Spaces.", T0.AddHours(2));

        var before = Assert.Single(FactStore.ReadAsOf(fixture.Connection, T0.AddHours(1)));
        Assert.Equal("Tabs.", before.Body);

        var after = Assert.Single(FactStore.ReadAsOf(fixture.Connection, T0.AddHours(3)));
        Assert.Equal("Spaces.", after.Body);
    }

    // The boundary is the whole point of a half-open window: at the instant of the change,
    // exactly one belief answers, not two and not zero.
    [Fact]
    public void ReadAsOf_AtTheExactInstantOfSupersession_ReturnsOnlyTheNewBelief()
    {
        using var fixture = new StoreFixture();
        var changed = T0.AddHours(2);

        fixture.Remember("prefers", "Tabs.");
        fixture.Remember("prefers", "Spaces.", changed);

        var atChange = Assert.Single(FactStore.ReadAsOf(fixture.Connection, changed));
        Assert.Equal("Spaces.", atChange.Body);
    }

    [Fact]
    public void Forget_ClosesTheFactWithoutReplacingIt()
    {
        using var fixture = new StoreFixture();
        var written = fixture.Remember("prefers", "Tabs.");

        var forgotten = FactStore.Forget(fixture.Connection, written.FactId, "asked to forget", T0.AddHours(1));

        Assert.True(forgotten);
        Assert.Empty(FactStore.ReadLive(fixture.Connection));

        // Closed, but still on disk — D8 forbids destroying authored truth.
        var history = Assert.Single(FactStore.History(fixture.Connection, StoreFixture.SubjectPath, "prefers"));
        Assert.NotNull(history.ValidTo);
        Assert.Null(history.SupersededBy);

        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM supersession WHERE new_fact_id IS NULL;"));
    }

    [Fact]
    public void Forget_OnAnAlreadyClosedFact_ChangesNothing()
    {
        using var fixture = new StoreFixture();
        var written = fixture.Remember("prefers", "Tabs.");
        FactStore.Forget(fixture.Connection, written.FactId, "first", T0.AddHours(1));

        var second = FactStore.Forget(fixture.Connection, written.FactId, "again", T0.AddHours(2));

        Assert.False(second);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM supersession;"));
    }

    [Fact]
    public void Forget_AfterFailing_LeavesNoPartialSupersessionRow()
    {
        using var fixture = new StoreFixture();

        Assert.False(FactStore.Forget(fixture.Connection, factId: 999, "no such fact", T0));

        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM supersession;"));
    }

    [Fact]
    public void Search_FindsAFactByAWordInItsBody()
    {
        using var fixture = new StoreFixture();
        fixture.Remember("prefers", "Hand-written SQL keeps query plans visible.");

        var hits = FactStore.Search(fixture.Connection, "query plans", limit: 10);

        Assert.Equal("Hand-written SQL keeps query plans visible.", Assert.Single(hits).Body);
    }

    // D3: the index holds live facts only. A superseded fact surfacing in search would put
    // a stale belief in front of the model as though it were current.
    //
    // What this actually guards is the eviction TRIGGER — deleting the query's own
    // `valid_to IS NULL` clause does not fail it, because the closed row is already gone
    // from the index by then. The clause is guarded separately, below.
    [Fact]
    public void Search_DoesNotReturnSupersededFacts()
    {
        using var fixture = new StoreFixture();
        fixture.Remember("prefers", "Tabs are the indentation choice.");
        fixture.Remember("prefers", "Spaces are the indentation choice.", T0.AddHours(1));

        var hits = FactStore.Search(fixture.Connection, "indentation", limit: 10);

        Assert.Equal("Spaces are the indentation choice.", Assert.Single(hits).Body);
    }

    // The backstop the trigger hides. A damaged or stale FTS index is a state D8 expects and
    // `repair` exists to fix, so search must not hand back a closed belief just because the
    // index still lists it. Re-inserting the closed row into fact_fts reproduces exactly that
    // damage, and the query's own live filter is the only thing left standing between it and
    // the caller.
    [Fact]
    public void Search_RefusesAClosedFactEvenWhenTheIndexStillListsIt()
    {
        using var fixture = new StoreFixture();
        var written = fixture.Remember("prefers", "Tabs are the indentation choice.");
        FactStore.Forget(fixture.Connection, written.FactId, "asked to forget", T0.AddHours(1));

        fixture.Execute(
            $"""
            INSERT INTO fact_fts(rowid, body, predicate)
            SELECT id, body, predicate FROM fact WHERE id = {written.FactId};
            """);

        Assert.Empty(FactStore.Search(fixture.Connection, "indentation", limit: 10));
    }

    [Fact]
    public void Search_DoesNotReturnForgottenFacts()
    {
        using var fixture = new StoreFixture();
        var written = fixture.Remember("prefers", "Tabs are the indentation choice.");
        FactStore.Forget(fixture.Connection, written.FactId, "asked to forget", T0.AddHours(1));

        Assert.Empty(FactStore.Search(fixture.Connection, "indentation", limit: 10));
    }

    // Raw user text reaching FTS5 unprocessed is either read as syntax the user did not
    // intend or rejected outright. A memory search that throws on an apostrophe is not a
    // memory search.
    //
    // Two mechanisms defend this, and exactly one input proves each — checked by breaking
    // them one at a time:
    //   "AND OR NOT"  fails if the per-token quoting goes (bare booleans become operators)
    //   "\"unbalanced" fails if the punctuation delimiters go (a quote inside a quoted token)
    // The remaining four survive both breaks today. They are here as regression cover for
    // ordinary things a person types, not as guards — do not read them as proving anything.
    [Theory]
    [InlineData("AND OR NOT")]
    [InlineData("\"unbalanced")]
    [InlineData("what's the plan?")]
    [InlineData("foo(bar)")]
    [InlineData("a * b")]
    [InlineData("NEAR/2")]
    public void Search_SurvivesQueryTextThatIsAlsoFtsSyntax(string query)
    {
        using var fixture = new StoreFixture();
        fixture.Remember("prefers", "Hand-written SQL keeps query plans visible.");

        var exception = Record.Exception(() => FactStore.Search(fixture.Connection, query, limit: 10));

        Assert.Null(exception);
    }

    [Fact]
    public void Search_OnQueryTextWithNoUsableTokens_ReturnsNothing()
    {
        using var fixture = new StoreFixture();
        fixture.Remember("prefers", "Hand-written SQL keeps query plans visible.");

        Assert.Empty(FactStore.Search(fixture.Connection, "*** !!! ***", limit: 10));
    }

    [Fact]
    public void ReadLive_FiltersByScope()
    {
        using var fixture = new StoreFixture();
        fixture.Remember("prefers", "Tabs.", scope: "user");
        fixture.Remember("uses", "dotnet 10.", scope: "project");

        var userFacts = Assert.Single(FactStore.ReadLive(fixture.Connection, scope: "user"));
        Assert.Equal("Tabs.", userFacts.Body);
    }

    [Fact]
    public void EnsureEntity_DerivesTheDisplayNameFromTheLastPathSegment()
    {
        using var fixture = new StoreFixture();

        FactStore.EnsureEntity(fixture.Connection, null, "/people/jim/preferences", "preference", 1);

        Assert.Equal("preferences", fixture.Scalar("SELECT name FROM entity WHERE path = '/people/jim/preferences';"));
    }

    private sealed class StoreFixture : IDisposable
    {
        public const string SubjectPath = "/people/jim";

        private readonly SandboxHome sandbox = new();

        public StoreFixture()
        {
            Connection = EngramDatabase.OpenInitialized(sandbox.Home);
        }

        public SqliteConnection Connection { get; }

        public RememberResult Remember(
            string predicate,
            string body,
            DateTimeOffset? at = null,
            string scope = "user",
            string reason = FactStore.DefaultSupersessionReason) =>
            FactStore.Remember(
                Connection,
                new FactWrite(SubjectPath, "person", predicate, body, scope, "stated"),
                at ?? T0,
                reason);

        public object? Scalar(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public void Execute(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Connection.Dispose();
            sandbox.Dispose();
        }
    }
}
