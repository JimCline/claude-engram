using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// Converting the JSON directory user facts used to live in, and replaying the result.
/// </summary>
/// <remarks>
/// Tier 2 rather than unit, because the claim worth testing is not what the converter returns but
/// what the store ends up holding: one live fact per belief, a closed predecessor behind each
/// restatement, and nothing that a second run would duplicate.
/// </remarks>
public class LegacyUserFactsTests
{
    private const string Session = "1f1572c1-fb0b-4d6d-9300-4fcb921ce657";

    private static string Entry(
        string id,
        string at,
        string kind,
        string? statement,
        string? supersedes = null,
        string? retracts = null)
    {
        var body = statement is null ? "null" : "\"" + statement.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var sup = supersedes is null ? "null" : "\"" + supersedes + "\"";
        var ret = retracts is null ? "null" : "\"" + retracts + "\"";

        return $$"""
                 {"id":"{{id}}","timestamp":"{{at}}","kind":"{{kind}}","statement":{{body}},"session_id":"{{Session}}","supersedes":{{sup}},"retracts":{{ret}}}
                 """;
    }

    [Fact]
    public void APersonalStatement_LandsWhereANativeCaptureWouldHavePutIt()
    {
        const string said = "I went to the movies last Saturday.";

        var import = LegacyUserFacts.Convert([Entry("u1", "2026-08-05T05:05:43.3962230Z", "personal", said)]);

        var fact = Assert.Single(import.Facts);

        // The whole reason this is C# and not a script: a migrated statement must be addressable
        // by the same fingerprint a fresh capture of the same sentence would compute.
        Assert.Equal(UserFacts.PathFor(UserFactTopic.AboutYou, said), fact.Subject);
        Assert.Equal(UserFacts.PredicateFor(UserFactTopic.AboutYou), fact.Predicate);
        Assert.Equal(UserFacts.StatementKind, fact.SubjectKind);
        Assert.Equal(UserFacts.Scope, fact.Scope);
        Assert.Equal(UserFacts.LearnedVia, fact.LearnedVia);
        Assert.Equal(said, fact.Body);
        Assert.Null(fact.ValidTo);
    }

    [Fact]
    public void ADirective_BecomesAnInstructionRatherThanSomethingAboutTheUser()
    {
        const string said = "Always give me the TL;DR first.";

        var import = LegacyUserFacts.Convert([Entry("u1", "2026-08-05T05:05:43Z", "directive", said)]);

        var fact = Assert.Single(import.Facts);
        Assert.Equal(UserFacts.PathFor(UserFactTopic.Instruction, said), fact.Subject);
        Assert.Equal("requires", fact.Predicate);
    }

    /// <summary>
    /// The case a naive converter gets wrong.
    /// </summary>
    /// <remarks>
    /// The old store linked a restatement to its predecessor by id, so the two texts differ and
    /// would fingerprint to different entities. Written at their own addresses they would both be
    /// live — two current beliefs where the user expressed one that changed. Every member of a
    /// chain therefore takes the root's address, which is what <c>UserFacts.Restate</c> does.
    /// </remarks>
    [Fact]
    public void ARestatement_TakesTheAddressOfWhatItReplaced()
    {
        const string first = "I work on games.";
        const string second = "I work on Godot games, mostly 2D.";

        var import = LegacyUserFacts.Convert(
        [
            Entry("u1", "2026-08-05T05:00:00Z", "personal", first),
            Entry("u2", "2026-08-05T06:00:00Z", "personal", second, supersedes: "u1"),
        ]);

        Assert.Equal(2, import.Facts.Count);
        Assert.Equal(1, import.Superseded);

        var expected = UserFacts.PathFor(UserFactTopic.AboutYou, first);
        Assert.All(import.Facts, fact => Assert.Equal(expected, fact.Subject));

        var older = import.Facts.Single(fact => fact.Body == first);
        var newer = import.Facts.Single(fact => fact.Body == second);

        Assert.Equal(newer.Id, older.SupersededBy);
        Assert.Equal(newer.ValidFrom, older.ValidTo);
        Assert.Null(newer.ValidTo);
    }

    [Fact]
    public void AThreeStepChain_LeavesOneOpenFactAtTheOriginalAddress()
    {
        var import = LegacyUserFacts.Convert(
        [
            Entry("u1", "2026-08-05T05:00:00Z", "personal", "one"),
            Entry("u2", "2026-08-05T06:00:00Z", "personal", "two", supersedes: "u1"),
            Entry("u3", "2026-08-05T07:00:00Z", "personal", "three", supersedes: "u2"),
        ]);

        var expected = UserFacts.PathFor(UserFactTopic.AboutYou, "one");
        Assert.All(import.Facts, fact => Assert.Equal(expected, fact.Subject));
        Assert.Single(import.Facts, fact => fact.ValidTo is null);
        Assert.Equal("three", import.Facts.Single(fact => fact.ValidTo is null).Body);
    }

    [Fact]
    public void ARetraction_ClosesItsTargetAndIsNotItselfAFact()
    {
        var import = LegacyUserFacts.Convert(
        [
            Entry("u1", "2026-08-05T05:00:00Z", "personal", "something I took back"),
            Entry("u2", "2026-08-05T06:00:00Z", "retraction", statement: null, retracts: "u1"),
        ]);

        var fact = Assert.Single(import.Facts);
        Assert.Equal(1, import.Retractions);
        Assert.Equal("something I took back", fact.Body);
        Assert.NotNull(fact.ValidTo);
        Assert.Null(fact.SupersededBy);
        Assert.Equal("the user retracted this", fact.SupersessionReason);
    }

    [Fact]
    public void ALinkPointingAtNothing_LeavesTheStatementOpenAndIsCounted()
    {
        var import = LegacyUserFacts.Convert(
        [
            Entry("u2", "2026-08-05T06:00:00Z", "personal", "orphan", supersedes: "u-that-never-existed"),
        ]);

        var fact = Assert.Single(import.Facts);
        Assert.Null(fact.ValidTo);
        Assert.Equal(1, import.DanglingLinks);
    }

    [Fact]
    public void ACycleInTheOldPointers_TerminatesInsteadOfHanging()
    {
        var import = LegacyUserFacts.Convert(
        [
            Entry("u1", "2026-08-05T05:00:00Z", "personal", "a", supersedes: "u2"),
            Entry("u2", "2026-08-05T06:00:00Z", "personal", "b", supersedes: "u1"),
        ]);

        Assert.Equal(2, import.Facts.Count);
    }

    [Fact]
    public void AnUnreadableFile_IsCountedRatherThanFailingTheWholeImport()
    {
        var import = LegacyUserFacts.Convert(
        [
            "{ this is not json",
            Entry("u1", "2026-08-05T05:00:00Z", "personal", "survivor"),
        ]);

        Assert.Equal(1, import.Skipped);
        Assert.Equal("survivor", Assert.Single(import.Facts).Body);
    }

    [Fact]
    public void Replayed_TheChainLeavesExactlyOneLiveFactForTheBelief()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var import = LegacyUserFacts.Convert(
        [
            Entry("u1", "2026-08-05T05:00:00Z", "personal", "I work on games."),
            Entry("u2", "2026-08-05T06:00:00Z", "personal", "I work on Godot games.", supersedes: "u1"),
        ]);

        var result = FactJournal.Replay(connection, import.Facts, apply: true);

        Assert.Equal(2, result.Written);
        Assert.Equal(0, result.Unresolved);

        var path = UserFacts.PathFor(UserFactTopic.AboutYou, "I work on games.");
        Assert.Equal(1, CountFacts(connection, path, liveOnly: true));
        Assert.Equal(2, CountFacts(connection, path, liveOnly: false));
    }

    [Fact]
    public void Replayed_Twice_WritesNothingTheSecondTime()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var import = LegacyUserFacts.Convert(
        [
            Entry("u1", "2026-08-05T05:00:00Z", "personal", "one"),
            Entry("u2", "2026-08-05T06:00:00Z", "personal", "two", supersedes: "u1"),
            Entry("u3", "2026-08-05T07:00:00Z", "retraction", statement: null, retracts: "u2"),
        ]);

        var first = FactJournal.Replay(connection, import.Facts, apply: true);
        var second = FactJournal.Replay(connection, import.Facts, apply: true);

        Assert.Equal(2, first.Written);
        Assert.Equal(0, second.Written);
        Assert.Equal(2, second.AlreadyPresent);
    }

    /// <summary>
    /// A migrated statement and a later native capture of the same sentence are one belief.
    /// </summary>
    /// <remarks>
    /// This is the payoff of computing the path with <c>UserFacts.PathFor</c> rather than
    /// inventing an address: <c>Capture</c> finds a live fact already at that entity and stays
    /// silent, instead of filing the same sentence a second time.
    /// </remarks>
    [Fact]
    public void AfterImport_SayingTheSameThingAgainIsRecognisedAsARepeat()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        const string said = "I prefer the TL;DR first.";
        FactJournal.Replay(
            connection,
            LegacyUserFacts.Convert([Entry("u1", "2026-08-05T05:00:00Z", "personal", said)]).Facts,
            apply: true);

        var captured = UserFacts.Capture(
            connection, UserFactTopic.AboutYou, said, Session, DateTimeOffset.UnixEpoch.AddSeconds(9_000_000));

        Assert.Null(captured);
    }

    private static int CountFacts(Microsoft.Data.Sqlite.SqliteConnection connection, string path, bool liveOnly)
    {
        using var command = connection.CreateCommand();
        command.CommandText = liveOnly
            ? "SELECT COUNT(*) FROM fact f JOIN entity e ON e.id = f.subject_id WHERE e.path = $p AND f.valid_to IS NULL;"
            : "SELECT COUNT(*) FROM fact f JOIN entity e ON e.id = f.subject_id WHERE e.path = $p;";
        command.Parameters.AddWithValue("$p", path);

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
