using System.Text.Json.Nodes;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The journal against real stores: what it writes, and whether what it writes can rebuild a store
/// the snapshots could not have restored.
/// </summary>
public sealed class FactJournalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static long Write(SqliteConnection connection, string path, string body, DateTimeOffset at) =>
        FactStore.Remember(connection, new FactWrite(path, "note", "states", body, "project", "stated"), at).FactId;

    private static IReadOnlyList<JournalFact> Reread(EngramHome home)
    {
        var facts = FactJournal.Parse(File.ReadLines(FactJournal.PathIn(home)), out var skipped);
        Assert.Equal(0, skipped);
        return facts;
    }

    private static IReadOnlyList<string> Bodies(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM fact ORDER BY id;";
        using var reader = command.ExecuteReader();

        var bodies = new List<string>();
        while (reader.Read())
        {
            bodies.Add(reader.GetString(0));
        }

        return bodies;
    }

    [Fact]
    public void Write_PutsEveryFactOnItsOwnLine()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "/project/a", "the first thing", T0);
        Write(connection, "/project/b", "the second thing", T0);

        var written = FactJournal.Write(connection, sandbox.Home, T0);

        Assert.Equal(2, written);
        var lines = File.ReadAllLines(FactJournal.PathIn(sandbox.Home));
        Assert.Equal(3, lines.Length);
        Assert.Contains("engram-facts", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeader_NamesTheSchemaThatWroteIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/project/a", "something", T0);

        FactJournal.Write(connection, sandbox.Home, T0);

        var header = JsonNode.Parse(File.ReadLines(FactJournal.PathIn(sandbox.Home)).First())!.AsObject();
        Assert.Equal(EngramDatabase.SchemaVersion, header["schema_version"]!.GetValue<int>());
        Assert.Equal(FactJournal.FormatVersion, header["format_version"]!.GetValue<int>());
    }

    [Fact]
    public void Write_CarriesTheFieldsAReplayNeeds()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        FactStore.Remember(
            connection,
            new FactWrite("/project/a", "note", "depends-on", "the body", "project", "observed", "src/x.cs:12"),
            T0);

        FactJournal.Write(connection, sandbox.Home, T0);

        var fact = Assert.Single(Reread(sandbox.Home));
        Assert.Equal("/project/a", fact.Subject);
        Assert.Equal("note", fact.SubjectKind);
        Assert.Equal("depends-on", fact.Predicate);
        Assert.Equal("the body", fact.Body);
        Assert.Equal("project", fact.Scope);
        Assert.Equal("observed", fact.LearnedVia);
        Assert.Equal("src/x.cs:12", fact.Evidence);
        Assert.Equal(T0.ToUnixTimeSeconds(), fact.ValidFrom);
        Assert.Null(fact.ValidTo);
    }

    [Fact]
    public void Write_IncludesFactsThatAreNoLongerBelieved()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "/project/a", "the old belief", T0);
        Write(connection, "/project/a", "the new belief", T0.AddHours(1));

        FactJournal.Write(connection, sandbox.Home, T0.AddHours(2));

        var facts = Reread(sandbox.Home);
        Assert.Equal(2, facts.Count);
        var closed = Assert.Single(facts, f => f.ValidTo is not null);
        Assert.Equal("the old belief", closed.Body);
        Assert.NotNull(closed.SupersededBy);
        Assert.NotNull(closed.SupersessionReason);
    }

    [Fact]
    public void Write_LeavesNoPartialFileBehind()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/project/a", "something", T0);

        FactJournal.Write(connection, sandbox.Home, T0);

        Assert.Empty(Directory.GetFiles(sandbox.Home.BackupDir, "*.partial"));
    }

    [Fact]
    public void Write_ReplacesTheJournalRatherThanGrowingIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Write(connection, "/project/a", "one", T0);
        FactJournal.Write(connection, sandbox.Home, T0);

        Write(connection, "/project/b", "two", T0.AddMinutes(1));
        FactJournal.Write(connection, sandbox.Home, T0.AddMinutes(1));

        Assert.Equal(2, Reread(sandbox.Home).Count);
    }

    [Fact]
    public void Replay_RebuildsAStoreThatWasEmpty()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the first thing", T0);
            Write(connection, "/project/b", "the second thing", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        var facts = Reread(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(2, result.Written);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(["the first thing", "the second thing"], Bodies(rebuilt));
    }

    [Fact]
    public void Replay_LeavesTheReplayedFactsFindable()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/parsers", "the tokenizer keeps its own line counter", T0);
            FactJournal.Write(connection, source.Home, T0);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        // The lexical index is maintained by triggers on `fact`, so a replayed fact is only
        // findable if it went in through a real insert rather than around one.
        using var command = rebuilt.CreateCommand();
        command.CommandText = "SELECT body FROM fact_fts WHERE fact_fts MATCH 'tokenizer';";
        Assert.Equal("the tokenizer keeps its own line counter", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void Replay_KeepsTheChainFromTheOldBeliefToTheNewOne()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the old belief", T0);
            Write(connection, "/project/a", "the new belief", T0.AddHours(1));
            FactJournal.Write(connection, source.Home, T0.AddHours(2));
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        using var command = rebuilt.CreateCommand();
        command.CommandText =
            """
            SELECT old.body, new.body, s.reason
            FROM supersession s
            JOIN fact old ON old.id = s.old_fact_id
            JOIN fact new ON new.id = s.new_fact_id;
            """;
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("the old belief", reader.GetString(0));
        Assert.Equal("the new belief", reader.GetString(1));
        Assert.False(reader.IsDBNull(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Replay_LeavesOnlyTheNewestBeliefLive()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the old belief", T0);
            Write(connection, "/project/a", "the new belief", T0.AddHours(1));
            FactJournal.Write(connection, source.Home, T0.AddHours(2));
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        using var command = rebuilt.CreateCommand();
        command.CommandText = "SELECT body FROM fact WHERE valid_to IS NULL;";
        Assert.Equal("the new belief", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void Replay_Twice_WritesNothingTheSecondTime()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the old belief", T0);
            Write(connection, "/project/a", "the new belief", T0.AddHours(1));
            FactJournal.Write(connection, source.Home, T0.AddHours(2));
        }

        var facts = Reread(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, facts, apply: true);
        var second = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(0, second.Written);
        Assert.Equal(2, second.AlreadyPresent);
        Assert.Equal(2, Bodies(rebuilt).Count);
    }

    [Fact]
    public void Replay_WithoutApply_ChangesNothing()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "something", T0);
            FactJournal.Write(connection, source.Home, T0);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var plan = FactJournal.Replay(rebuilt, Reread(source.Home), apply: false);

        Assert.Equal(1, plan.Written);
        Assert.Empty(Bodies(rebuilt));
    }

    [Fact]
    public void Replay_LeavesFactsTheStoreAlreadyHadAlone()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "shared", T0);
            Write(connection, "/project/b", "only in the journal", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        Write(rebuilt, "/project/a", "shared", T0);

        var result = FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(["shared", "only in the journal"], Bodies(rebuilt));
    }

    [Fact]
    public void Replay_CarriesTheOriginalTimestampsRatherThanStampingNow()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "something", T0);
            FactJournal.Write(connection, source.Home, T0);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        using var command = rebuilt.CreateCommand();
        command.CommandText = "SELECT valid_from FROM fact;";
        Assert.Equal(T0.ToUnixTimeSeconds(), (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Parse_SkipsAMangledLineRatherThanLosingTheRest()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Write(connection, "/project/a", "the first thing", T0);
        Write(connection, "/project/b", "the second thing", T0.AddMinutes(1));
        FactJournal.Write(connection, sandbox.Home, T0.AddMinutes(2));

        var path = FactJournal.PathIn(sandbox.Home);
        var lines = File.ReadAllLines(path);
        lines[1] = lines[1][..(lines[1].Length / 2)];
        File.WriteAllLines(path, lines);

        var facts = FactJournal.Parse(File.ReadLines(path), out var skipped);

        Assert.Equal(1, skipped);
        Assert.Equal("the second thing", Assert.Single(facts).Body);
    }

    [Fact]
    public void Parse_IgnoresBlankLines()
    {
        var facts = FactJournal.Parse(["", "   ", ""], out var skipped);

        Assert.Empty(facts);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Replay_IntoAStoreAtANewerSchema_StillLands()
    {
        // The point of the whole tier: a `.db` snapshot from an older schema is refused, but the
        // text is addressed by path and predicate, so it does not care what version wrote it.
        using var sandbox = new SandboxHome(initialize: false);
        Directory.CreateDirectory(sandbox.Home.BackupDir);
        File.WriteAllLines(
            FactJournal.PathIn(sandbox.Home),
            [
                """{"format":"engram-facts","format_version":1,"schema_version":1,"written_at":"2026-01-01T00:00:00Z"}""",
                """{"id":7,"subject":"/project/a","kind":"note","predicate":"states","body":"written by an older engram","scope":"project","learned_via":"stated","valid_from":1767225600,"created_at":1767225600}""",
            ]);

        var facts = FactJournal.Parse(File.ReadLines(FactJournal.PathIn(sandbox.Home)), out var skipped);
        Assert.Equal(0, skipped);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        FactJournal.Replay(connection, facts, apply: true);

        Assert.Equal(["written by an older engram"], Bodies(connection));
    }

    [Fact]
    public void Replay_OfAFactWhoseSupersederIsMissing_KeepsTheFactAndReportsTheGap()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var facts = new List<JournalFact>
        {
            new(7, "/project/a", "note", "states", "closed, but by what?", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 99, "revised", T0.ToUnixTimeSeconds()),
        };

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var result = FactJournal.Replay(connection, facts, apply: true);

        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Unresolved);
        Assert.Equal(["closed, but by what?"], Bodies(connection));
    }

    // ---- a target that believes something else ----

    /// <summary>
    /// The reported defect. <c>ux_fact_live</c> permits one live fact per subject and predicate, so
    /// a journalled belief that disagrees with the target's cannot be inserted — and the insert used
    /// to raise SQLITE_CONSTRAINT and abort the whole replay, recovering nothing.
    /// </summary>
    [Fact]
    public void Replay_WhenTheTargetBelievesSomethingElse_SkipsThatFactRatherThanFailing()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "what the journal remembers", T0);
            Write(connection, "/project/b", "only in the journal", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        Write(rebuilt, "/project/a", "what the store believes now", T0.AddHours(1));

        var result = FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        Assert.Equal(1, result.Conflicted);
        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.AlreadyPresent);

        // The target's belief is untouched — replay may never close one to make room (D8) — and
        // everything that did not collide was still recovered.
        Assert.Equal(["what the store believes now", "only in the journal"], Bodies(rebuilt));
    }

    /// <summary>
    /// The shape the bug was found in: a journal replayed into a home that has been through
    /// <c>init</c>, so the seeded corpus is already there under the same subjects and predicates.
    /// Before the fix this recovered nothing at all.
    /// </summary>
    [Fact]
    public void Replay_IntoAnInitialisedHome_RecoversWhatDoesNotCollide()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/mine", "a fact only this store has", T0);
            FactJournal.Write(connection, source.Home, T0);
        }

        // Initialised, so the store arrives holding the canned corpus rather than nothing.
        using var target = new SandboxHome();
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var before = Bodies(rebuilt).Count;
        Assert.True(before > 0, "an initialised home was expected to arrive seeded");

        var journal = Reread(source.Home);
        foreach (var seeded in FactJournal.Read(rebuilt).Take(3))
        {
            // Same subject and predicate, different body: what a second install's seed looks like
            // to the first install's journal.
            journal = [.. journal, seeded with { Body = seeded.Body + " (as the journal had it)" }];
        }

        var result = FactJournal.Replay(rebuilt, journal, apply: true);

        Assert.Equal(3, result.Conflicted);
        Assert.Equal(1, result.Written);
        Assert.Equal(before + 1, Bodies(rebuilt).Count);
    }

    /// <summary>
    /// A dry run that under-reports conflicts would promise a recovery the apply cannot deliver.
    /// </summary>
    [Fact]
    public void Replay_ADryRun_ReportsTheSameConflictsTheApplyFinds()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "what the journal remembers", T0);
            Write(connection, "/project/b", "only in the journal", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        Write(rebuilt, "/project/a", "what the store believes now", T0.AddHours(1));

        var facts = Reread(source.Home);
        var dry = FactJournal.Replay(rebuilt, facts, apply: false);
        var applied = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(applied.Conflicted, dry.Conflicted);
        Assert.Equal(applied.Written, dry.Written);
    }

    /// <summary>
    /// Only live facts can collide — the uniqueness index is partial on <c>valid_to IS NULL</c> —
    /// so a closed fact lands beside whatever the target believes now and adds to the record of how
    /// the belief got there rather than competing with it.
    /// </summary>
    [Fact]
    public void Replay_AClosedJournalFact_LandsBesideTheTargetsLiveBelief()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "what was believed first", T0);
            Write(connection, "/project/a", "what replaced it", T0.AddMinutes(1));
            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        Write(rebuilt, "/project/a", "what this store believes", T0.AddHours(1));

        var result = FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        // The closed one is written, the live one collides.
        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Conflicted);
        Assert.Contains("what was believed first", Bodies(rebuilt));
    }

    /// <summary>
    /// Two live facts for one subject and predicate can only reach a journal through a merged
    /// bundle, and the second must be reported rather than taking the replay down at the index.
    /// </summary>
    /// <remarks>
    /// The dry run is the half that has to be asserted, and the first version of this test asserted
    /// only the apply — which passed with the in-journal check deleted, because an apply sees its
    /// own inserts through the transaction and resolves the collision without it. A dry run inserts
    /// nothing, so it is the only caller that needs the pairs tracked, and the only one whose
    /// numbers change when the tracking goes.
    /// </remarks>
    [Fact]
    public void Replay_AJournalHoldingTwoLiveFactsForOneSubject_KeepsTheFirstAndReportsTheSecond()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the one that arrived first", T0);
            FactJournal.Write(connection, source.Home, T0);
        }

        var facts = Reread(source.Home);
        IReadOnlyList<JournalFact> merged =
            [.. facts, .. facts.Select(f => f with { Id = f.Id + 1000, Body = "a second opinion" })];

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var dry = FactJournal.Replay(rebuilt, merged, apply: false);
        Assert.Equal(1, dry.Written);
        Assert.Equal(1, dry.Conflicted);

        var applied = FactJournal.Replay(rebuilt, merged, apply: true);
        Assert.Equal(1, applied.Written);
        Assert.Equal(1, applied.Conflicted);
        Assert.Equal(["the one that arrived first"], Bodies(rebuilt));
    }
}
