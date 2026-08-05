using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// User facts against the real store, which is the point of the migration: these behaviours
/// used to be a second implementation of validity windows in JSON, and every one of them is
/// now the <c>fact</c> table's own rule being exercised rather than a parallel one being
/// re-tested.
/// </summary>
public class UserFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Live captures only. Keyed on the subtree rather than on scope, because the seed
    /// corpus is scoped <c>user</c> too — it is memory about how this user works — so a
    /// scope filter would quietly match all fifty-one seeded facts as well.
    /// </summary>
    private static IReadOnlyList<StoredFact> LiveCaptures(SqliteConnection connection) =>
        FactStore.ReadSubtree(connection, UserFacts.Root);

    [Fact]
    public void CapturedStatementsReachRecallAsLongTermFacts()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        UserFacts.Capture(connection, UserFactTopic.AboutYou, "I went to see a Spiderman movie last Saturday", "sess-1", Now);
        UserFacts.Capture(connection, UserFactTopic.Instruction, "always use BEGIN IMMEDIATE", "sess-1", Now);

        var facts = FactCatalog.ReadLongTerm(sandbox.Home, Now);

        Assert.Contains(facts, f => f.Predicate == "stated" && f.Body.Contains("Spiderman") && f.Scope == "user");
        Assert.Contains(facts, f => f.Predicate == "requires" && f.Body.Contains("BEGIN IMMEDIATE") && f.Scope == "user");
    }

    // The primer names topics out loud, so a user fact landing in the seed corpus's
    // "memory" bucket would be visible to the user as memory forgetting what kind of thing
    // it is holding.
    [Fact]
    public void CapturedStatementsCarryTheirOwnTopicNames()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        UserFacts.Capture(connection, UserFactTopic.AboutYou, "I grew up in Fort Collins", "sess-1", Now);
        UserFacts.Capture(connection, UserFactTopic.Instruction, "always use BEGIN IMMEDIATE", "sess-1", Now);

        var facts = FactCatalog.ReadLongTerm(sandbox.Home, Now);

        Assert.Contains(facts, f => f.Body.Contains("Fort Collins") && f.Topic == "about you");
        Assert.Contains(facts, f => f.Body.Contains("BEGIN IMMEDIATE") && f.Topic == "your standing instructions");
    }

    // The delete key. Capturing what someone says about their life without a way to take
    // it back is not something this should ever do.
    [Fact]
    public void ARetractedFactStopsBeingRecalled()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I saw a film last Saturday", "sess-1", Now);

        Assert.True(FactStore.Forget(connection, factId!.Value, "retracted by the user", Now));

        Assert.DoesNotContain(FactCatalog.ReadLongTerm(sandbox.Home, Now), f => f.Body.Contains("film"));
    }

    // Facts are closed, never erased (CLAUDE.md: append-only). That someone took a fact
    // back is history too, and the row has to survive to say so.
    [Fact]
    public void RetractionClosesTheFactWithoutDeletingAnything()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I saw a film last Saturday", "sess-1", Now)!.Value;
        FactStore.Forget(connection, factId, "retracted by the user", Now);

        var stored = FactStore.ReadById(connection, factId);

        Assert.NotNull(stored);
        Assert.Equal("I saw a film last Saturday", stored.Body);
        Assert.NotNull(stored.ValidTo);
        Assert.Null(stored.SupersededBy);
    }

    // The model rewriting "last Saturday" into a date must replace the raw capture, not
    // sit beside it. Two near-identical facts is how a memory store starts lying about
    // how much it knows.
    [Fact]
    public void ARestatementReplacesTheCaptureRatherThanDuplicatingIt()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var captureId = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I saw a movie last Saturday", "sess-1", Now)!.Value;
        var replacementId = UserFacts.Restate(connection, captureId, "Saw a Spider-Man film on 2026-08-01", "sess-1", Now);

        Assert.NotNull(replacementId);

        var fact = Assert.Single(LiveCaptures(connection));
        Assert.Equal("Saw a Spider-Man film on 2026-08-01", fact.Body);
        Assert.Equal(replacementId.Value, fact.Id);

        Assert.Contains(FactCatalog.ReadLongTerm(sandbox.Home, Now), f => f.Id == FactCatalog.HandleFor(fact.Id));
    }

    // Supersession is the whole reason this data moved: the JSON store recorded that a
    // capture was replaced, but not as a queryable link between the two.
    [Fact]
    public void ARestatementLinksBackToWhatItReplaced()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var captureId = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I saw a movie last Saturday", "sess-1", Now)!.Value;
        var replacementId = UserFacts.Restate(connection, captureId, "Saw a Spider-Man film on 2026-08-01", "sess-1", Now)!.Value;

        var original = FactStore.ReadById(connection, captureId)!;

        Assert.Equal(replacementId, original.SupersededBy);
        Assert.NotNull(original.ValidTo);
    }

    [Fact]
    public void RestatingSomethingThatIsNotLiveReportsFailureRatherThanWritingAnyway()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var captureId = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I saw a film last Saturday", "sess-1", Now)!.Value;
        FactStore.Forget(connection, captureId, "retracted by the user", Now);

        Assert.Null(UserFacts.Restate(connection, captureId, "Saw a film on 2026-08-01", "sess-1", Now));
        Assert.Null(UserFacts.Restate(connection, 9999, "about a fact that does not exist", "sess-1", Now));
    }

    // The hook fires on every message, so a user who repeats themselves must not accumulate
    // duplicates. The JSON store could not tell a repeat from a new statement at all.
    [Fact]
    public void SayingTheSameThingTwiceCapturesItOnce()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var first = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I use a Dvorak keyboard", "sess-1", Now);
        var second = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I use a Dvorak keyboard.", "sess-2", Now);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(LiveCaptures(connection));
    }

    // The regression this guards is subtle and would look like memory getting worse over
    // time: the model rewrites a capture into something legible, the user says the original
    // sentence again, and the rewrite is dragged back to the raw version.
    [Fact]
    public void RepeatingAStatementDoesNotUndoItsRewrite()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var captureId = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I saw a movie last Saturday", "sess-1", Now)!.Value;
        UserFacts.Restate(connection, captureId, "Saw a Spider-Man film on 2026-08-01", "sess-1", Now);

        Assert.Null(UserFacts.Capture(connection, UserFactTopic.AboutYou, "I saw a movie last Saturday", "sess-2", Now));

        var fact = Assert.Single(LiveCaptures(connection));
        Assert.Equal("Saw a Spider-Man film on 2026-08-01", fact.Body);
    }

    // The asymmetry with the seed corpus is deliberate: a re-seed is nobody asking for a
    // forgotten fact back, and a user typing the sentence again is.
    [Fact]
    public void SayingSomethingAgainAfterRetractingItCapturesItAfresh()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var first = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I use a Dvorak keyboard", "sess-1", Now)!.Value;
        FactStore.Forget(connection, first, "retracted by the user", Now);

        var second = UserFacts.Capture(connection, UserFactTopic.AboutYou, "I use a Dvorak keyboard", "sess-2", Now);

        Assert.NotNull(second);
        Assert.NotEqual(first, second.Value);
    }

    // Provenance is what makes "where did this come from" answerable once the conversation
    // is gone, and the column is a foreign key, so a bare host session string cannot fill it.
    [Fact]
    public void ACaptureIsAnchoredToTheSessionItArrivedIn()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        UserFacts.Capture(connection, UserFactTopic.AboutYou, "I grew up in Fort Collins", "sess-abc", Now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.external_id FROM fact f JOIN session s ON s.id = f.session_id
             WHERE f.scope = 'user';
            """;

        Assert.Equal("sess-abc", command.ExecuteScalar() as string);
    }

    [Fact]
    public void RepeatedCapturesInOneSessionShareOneSessionRow()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        UserFacts.Capture(connection, UserFactTopic.AboutYou, "I grew up in Fort Collins", "sess-abc", Now);
        UserFacts.Capture(connection, UserFactTopic.AboutYou, "I use a Dvorak keyboard", "sess-abc", Now);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session;";

        Assert.Equal(1L, command.ExecuteScalar());
    }

    // Case and punctuation are not what makes two statements different; if they were, the
    // repeat check above would only fire on a byte-identical retype.
    [Fact]
    public void FingerprintIgnoresCaseAndPunctuation()
    {
        Assert.Equal(UserFacts.Fingerprint("I use Dvorak."), UserFacts.Fingerprint("i  use   dvorak"));
        Assert.NotEqual(UserFacts.Fingerprint("I use Dvorak"), UserFacts.Fingerprint("I use Colemak"));
    }
}
