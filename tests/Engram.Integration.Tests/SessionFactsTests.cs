using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Session notes against the real store. The JSONL file they used to live in had no notion of
/// a closed record at all, which made a session note the one kind of memory that could be
/// written and never taken back; most of what follows is that gap, plus the addressing the
/// move needed.
/// </summary>
public class SessionFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANoteIsReadBackForTheSessionThatTookIt()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(connection, "sess-a", "The WAL checkpoint runs on close.", subject: null, evidence: null, agent: null, Now);

        var (current, prior) = SessionFacts.Read(connection, "sess-a", Now);

        var fact = Assert.Single(current);
        Assert.Equal("The WAL checkpoint runs on close.", fact.Statement);
        Assert.Empty(prior);
    }

    [Fact]
    public void AnotherSessionsNoteIsReadBackAsPrior()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(connection, "sess-a", "Note from a.", subject: null, evidence: null, agent: null, Now);
        SessionFacts.Append(connection, "sess-b", "Note from b.", subject: null, evidence: null, agent: null, Now);

        var (current, prior) = SessionFacts.Read(connection, "sess-a", Now);

        Assert.Equal("Note from a.", Assert.Single(current).Statement);
        Assert.Equal("Note from b.", Assert.Single(prior).Statement);
    }

    // The defect this migration exists to fix. In the JSONL format there was no way to
    // express a retracted note, so a mistaken one stayed recallable forever.
    [Fact]
    public void ANoteCanBeRetractedAndStopsBeingRead()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = SessionFacts.Append(
            connection, "sess-a", "Concluded the timeout was the cause.", subject: null, evidence: null, agent: null, Now);

        Assert.True(FactStore.Forget(connection, factId, "retracted by the user", Now));

        var (current, prior) = SessionFacts.Read(connection, "sess-a", Now);
        Assert.Empty(current);
        Assert.Empty(prior);
    }

    // A retracted note stays retracted for later sessions too — the prior-session tier reads
    // the same live set, so there is no second path that could resurrect it.
    [Fact]
    public void ARetractedNoteDoesNotReappearInALaterSession()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = SessionFacts.Append(
            connection, "sess-a", "Concluded the timeout was the cause.", subject: null, evidence: null, agent: null, Now);
        FactStore.Forget(connection, factId, "retracted by the user", Now);

        var (_, prior) = SessionFacts.Read(connection, "sess-later", Now);

        Assert.Empty(prior);
    }

    // Re-recording something already recorded has learned nothing, and the alternative to
    // returning the existing handle is a supersession row asserting a belief changed when the
    // text is identical.
    [Fact]
    public void RepeatingAStatementInOneSessionReturnsTheSameHandleAndWritesOneFact()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var first = SessionFacts.Append(connection, "sess-a", "Retries are capped at three.", subject: null, evidence: null, agent: null, Now);
        var second = SessionFacts.Append(connection, "sess-a", "retries are capped at three", subject: null, evidence: null, agent: null, Now);

        Assert.Equal(first, second);

        var history = FactStore.History(
            connection,
            SessionFacts.PathFor(SessionIdOf(connection, "sess-a"), agent: null, "Retries are capped at three."),
            SessionFacts.Predicate);

        Assert.Single(history);
    }

    // Two sessions reaching the same conclusion are two observations, not one. They share a
    // fingerprint, so only the session segment in the path keeps them apart — get that wrong
    // and the second session's note supersedes the first's.
    [Fact]
    public void TheSameStatementInTwoSessionsIsTwoNotes()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var a = SessionFacts.Append(connection, "sess-a", "Retries are capped at three.", subject: null, evidence: null, agent: null, Now);
        var b = SessionFacts.Append(connection, "sess-b", "Retries are capped at three.", subject: null, evidence: null, agent: null, Now);

        Assert.NotEqual(a, b);

        var (current, prior) = SessionFacts.Read(connection, "sess-a", Now);
        Assert.Single(current);
        Assert.Single(prior);
    }

    // A subagent's name survives the trip through the path, which only carries a slug —
    // "task-gopher:task-gopher" and "task gopher task gopher" slug identically.
    [Fact]
    public void ASubagentsNoteIsAttributedWithTheNameItGave()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(
            connection, "sess-a", "The build fails on net9.0.", subject: null, evidence: null, agent: "task-gopher:task-gopher", Now);

        var (current, _) = SessionFacts.Read(connection, "sess-a", Now);

        Assert.Equal("task-gopher:task-gopher", Assert.Single(current).Agent);
    }

    [Fact]
    public void TwoSubagentsInOneSessionKeepTheirOwnNotes()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(connection, "sess-a", "Found the caller.", subject: null, evidence: null, agent: "explorer", Now);
        SessionFacts.Append(connection, "sess-a", "Found the caller.", subject: null, evidence: null, agent: "reviewer", Now);

        var (current, _) = SessionFacts.Read(connection, "sess-a", Now);

        Assert.Equal(2, current.Count);
        Assert.Contains(current, f => f.Agent == "explorer");
        Assert.Contains(current, f => f.Agent == "reviewer");
    }

    [Fact]
    public void ASubjectIsReportedWhenGivenAndNotInventedWhenNot()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(connection, "sess-a", "Caps retries at three.", subject: "the retry policy", evidence: null, agent: null, Now);
        SessionFacts.Append(connection, "sess-a", "The build fails on net9.0.", subject: null, evidence: null, agent: null, Now);

        var (current, _) = SessionFacts.Read(connection, "sess-a", Now);

        Assert.Equal("the retry policy", Assert.Single(current, f => f.Statement.StartsWith("Caps", StringComparison.Ordinal)).Subject);
        Assert.Null(Assert.Single(current, f => f.Statement.StartsWith("The build", StringComparison.Ordinal)).Subject);
    }

    // Recall ranks working memory in its own tier and the primer names topics out loud. A
    // note that also came back as long-term memory would appear in recall beside itself, and
    // the primer would announce a session row id as a subject area.
    [Fact]
    public void NotesDoNotAlsoAppearInTheLongTermTier()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(connection, "sess-a", "The WAL checkpoint runs on close.", subject: null, evidence: null, agent: null, Now);

        var longTerm = FactCatalog.ReadLongTerm(connection, Now);

        Assert.DoesNotContain(longTerm, f => f.Body == "The WAL checkpoint runs on close.");
    }

    // Recall is a read. Ensuring the session row here would make every query a write against
    // the same lock the writers contend for, on a path that runs on every question asked.
    [Fact]
    public void ReadingForAnUnknownSessionCreatesNoSessionRow()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Read(connection, "never-written-to", Now);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void AgeIsCountedFromWhenTheNoteWasTaken()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(connection, "sess-a", "Noted three days ago.", subject: null, evidence: null, agent: null, Now.AddDays(-3));

        var (_, prior) = SessionFacts.Read(connection, "sess-b", Now);

        Assert.Equal(3, Assert.Single(prior).AgeDays);
    }

    // Nothing else writes under this root, but "skip what does not parse" is the difference
    // between one stray row and a whole tier throwing on read.
    [Fact]
    public void AFactParkedUnderTheRootByHandIsSkippedRatherThanThrowing()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        SessionFacts.Append(connection, "sess-a", "A well-formed note.", subject: null, evidence: null, agent: null, Now);

        FactStore.Remember(
            connection,
            new FactWrite(
                SubjectPath: SessionFacts.Root + "/not-a-row-id/deadbeef",
                SubjectKind: SessionFacts.NoteKind,
                Predicate: SessionFacts.Predicate,
                Body: "Parked by something that is not Append.",
                Scope: SessionFacts.Scope,
                LearnedVia: SessionFacts.LearnedVia),
            Now);

        var (current, prior) = SessionFacts.Read(connection, "sess-a", Now);

        Assert.Equal("A well-formed note.", Assert.Single(current).Statement);
        Assert.Empty(prior);
    }

    private static long SessionIdOf(SqliteConnection connection, string externalId) =>
        SessionStore.FindSession(connection, externalId)
        ?? throw new InvalidOperationException($"no session row for '{externalId}'");
}
