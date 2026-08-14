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

    private static long Write(SqliteConnection connection, string path, string body, DateTimeOffset at, string? details = null) =>
        FactStore.Remember(connection, new FactWrite(path, "note", "states", body, "project", "stated", Details: details), at).FactId;

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
    public void Write_AndReplay_CarriesDetailsIntact()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "the short version", T0, details: "the long version, verbatim.");
            FactJournal.Write(connection, source.Home, T0);
        }

        var facts = Reread(source.Home);
        Assert.Equal("the long version, verbatim.", Assert.Single(facts).Details);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        FactJournal.Replay(rebuilt, facts, apply: true);

        var replayed = Assert.Single(FactStore.ReadLive(rebuilt));
        Assert.Equal("the long version, verbatim.", replayed.Details);
    }

    [Fact]
    public void Parse_AJournalLineWrittenBeforeDetailsExisted_ParsesWithDetailsNull()
    {
        var facts = FactJournal.Parse(
            [
                """{"id":7,"subject":"/project/a","kind":"note","predicate":"states","body":"written before details existed","scope":"project","learned_via":"stated","valid_from":1767225600,"created_at":1767225600}""",
            ],
            out var skipped);

        Assert.Equal(0, skipped);
        Assert.Null(Assert.Single(facts).Details);
    }

    /// <summary>
    /// Replay identity is subject + predicate + body + <c>valid_from</c> — details rides along on
    /// insert but is never compared, so a body match with differing details is still
    /// <c>AlreadyPresent</c> rather than a conflict, and the target's own details survive untouched.
    /// </summary>
    [Fact]
    public void Replay_WhenBodyMatchesButDetailsDiffer_CountsAlreadyPresentAndLeavesTheTargetUntouched()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/project/a", "shared body", T0, details: "the journal's details");
            FactJournal.Write(connection, source.Home, T0);
        }

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);
        var targetId = Write(rebuilt, "/project/a", "shared body", T0, details: "the target's own details");

        var result = FactJournal.Replay(rebuilt, Reread(source.Home), apply: true);

        Assert.Equal(0, result.Written);
        Assert.Equal(1, result.AlreadyPresent);

        var targetFact = FactStore.ReadById(rebuilt, targetId);
        Assert.NotNull(targetFact);
        Assert.Equal("the target's own details", targetFact.Details);
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

    // ---- D68: a supersession may only be written into a row this replay inserted ----

    private static long SeedRawFact(
        SqliteConnection connection,
        string path,
        string predicate,
        string body,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo = null,
        long? supersededBy = null)
    {
        using var entity = connection.CreateCommand();
        entity.CommandText =
            """
            INSERT INTO entity (path, kind, name, created_at) VALUES ($path, 'note', $name, $createdAt)
            ON CONFLICT(path) DO UPDATE SET path = excluded.path
            RETURNING id;
            """;
        entity.Parameters.AddWithValue("$path", path);
        entity.Parameters.AddWithValue("$name", path);
        entity.Parameters.AddWithValue("$createdAt", validFrom.ToUnixTimeSeconds());
        var subjectId = (long)entity.ExecuteScalar()!;

        using var fact = connection.CreateCommand();
        fact.CommandText =
            """
            INSERT INTO fact (subject_id, predicate, body, path, scope, learned_via, regenerable,
                              valid_from, valid_to, superseded_by, created_at)
            VALUES ($subject, $predicate, $body, $path, 'project', 'stated', 0,
                    $validFrom, $validTo, $supersededBy, $createdAt)
            RETURNING id;
            """;
        fact.Parameters.AddWithValue("$subject", subjectId);
        fact.Parameters.AddWithValue("$predicate", predicate);
        fact.Parameters.AddWithValue("$body", body);
        fact.Parameters.AddWithValue("$path", path);
        fact.Parameters.AddWithValue("$validFrom", validFrom.ToUnixTimeSeconds());
        fact.Parameters.AddWithValue("$validTo", (object?)validTo?.ToUnixTimeSeconds() ?? DBNull.Value);
        fact.Parameters.AddWithValue("$supersededBy", (object?)supersededBy ?? DBNull.Value);
        fact.Parameters.AddWithValue("$createdAt", validFrom.ToUnixTimeSeconds());
        var factId = (long)fact.ExecuteScalar()!;

        if (validTo is null)
        {
            FactTokenIndex.Add(connection, null!, factId);
        }

        return factId;
    }

    private static void SeedSupersession(
        SqliteConnection connection, long oldFactId, long newFactId, DateTimeOffset at, string reason = "test fixture")
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO supersession (old_fact_id, new_fact_id, reason, created_at)
            VALUES ($old, $new, $reason, $createdAt);
            """;
        command.Parameters.AddWithValue("$old", oldFactId);
        command.Parameters.AddWithValue("$new", newFactId);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$createdAt", at.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    private static long? SupersededByOf(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT superseded_by FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() as long?;
    }

    private static long? ValidToOf(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT valid_to FROM fact WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() as long?;
    }

    private static int SupersessionRowCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM supersession;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private static int FactRowCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fact;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private static int FactRowCountFor(SqliteConnection connection, string path, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM fact f JOIN entity e ON e.id = f.subject_id
            WHERE e.path = $path AND f.body = $body;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$body", body);
        return (int)(long)command.ExecuteScalar()!;
    }

    private static bool HasTokenRow(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM fact_token WHERE fact_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Test 1 of D68 §7: a planted duplicate pair, one closed-and-superseded, one live, sharing
    /// (subject, predicate, body, valid_from). The address resolves to the live row (the only
    /// basis D68 §4.2 permits), and the target already disagrees with what the journal claims
    /// about it, so the outcome is a decline — nothing about either row's chain moves.
    /// </summary>
    [Fact]
    public void Replay_ADuplicatePairWithOneLiveOneClosed_LeavesTheChainAndTheLiveRowUnchanged()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var supersederId = SeedRawFact(rebuilt, "/project/other", "states", "the original superseder", T0);
        var closedId = SeedRawFact(
            rebuilt, "/project/dup", "states", "a duplicated belief", T0,
            validTo: T0.AddHours(1), supersededBy: supersederId);
        SeedSupersession(rebuilt, closedId, supersederId, T0.AddHours(1));
        var liveId = SeedRawFact(rebuilt, "/project/dup", "states", "a duplicated belief", T0);

        var factRowsBefore = FactRowCount(rebuilt);
        var supersessionRowsBefore = SupersessionRowCount(rebuilt);

        var facts = new List<JournalFact>
        {
            new(1, "/project/dup", "note", "states", "a duplicated belief", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 2, "revised",
                T0.ToUnixTimeSeconds()),
            new(2, "/project/fresh", "note", "states", "a fact only the journal has", null, null, "project",
                "stated", false, null, T0.AddMinutes(5).ToUnixTimeSeconds(), null, null, null,
                T0.AddMinutes(5).ToUnixTimeSeconds()),
        };

        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, result.Conflicted);
        Assert.Equal(supersederId, SupersededByOf(rebuilt, closedId));
        Assert.Null(SupersededByOf(rebuilt, liveId));
        Assert.Null(ValidToOf(rebuilt, liveId));
        Assert.Equal(supersessionRowsBefore, SupersessionRowCount(rebuilt));
        Assert.True(HasTokenRow(rebuilt, liveId));

        // Only the fresh fact was written; the ambiguous pair was left exactly as seeded.
        Assert.Equal(factRowsBefore + 1, FactRowCount(rebuilt));
    }

    /// <summary>
    /// Test 2 of D68 §7: replaying into a store that already fully agrees with the journal — its
    /// fact rows and its supersession row already match — is a genuine fixed point. Two separate
    /// <see cref="FactJournal.Replay"/> invocations (not one transaction reused: <c>claimed</c> is
    /// dry-run-only, per the existing "two live facts" test) must return byte-for-byte identical
    /// results, and change no row.
    /// </summary>
    [Fact]
    public void Replay_TwiceAgainstAnAlreadyLinkedStore_ReturnsIdenticalCountsBothTimes()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var newId = SeedRawFact(rebuilt, "/project/a", "states", "the new belief", T0.AddHours(1));
        var oldId = SeedRawFact(
            rebuilt, "/project/a", "states", "the old belief", T0,
            validTo: T0.AddHours(1), supersededBy: newId);
        SeedSupersession(rebuilt, oldId, newId, T0.AddHours(1));

        var facts = new List<JournalFact>
        {
            new(1, "/project/a", "note", "states", "the old belief", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 2, "revised",
                T0.ToUnixTimeSeconds()),
            new(2, "/project/a", "note", "states", "the new belief", null, null, "project", "stated",
                false, null, T0.AddHours(1).ToUnixTimeSeconds(), null, null, null,
                T0.AddHours(1).ToUnixTimeSeconds()),
        };

        var factRowsBefore = FactRowCount(rebuilt);
        var supersessionRowsBefore = SupersessionRowCount(rebuilt);

        var first = FactJournal.Replay(rebuilt, facts, apply: true);
        var second = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(first, second);
        Assert.Equal(0, second.Written);
        Assert.Equal(factRowsBefore, FactRowCount(rebuilt));
        Assert.Equal(supersessionRowsBefore, SupersessionRowCount(rebuilt));
        Assert.Equal(newId, SupersededByOf(rebuilt, oldId));
    }

    /// <summary>
    /// Test 3 of D68 §7 — the falsification for the rejected row-state guard (§3.1). A fact closed
    /// with <c>superseded_by</c> NULL and no <c>supersession</c> row is exactly the shape
    /// <c>Forget</c> leaves, and is structurally identical to a row this replay just inserted — only
    /// provenance tells them apart. Proved to fail under the rejected guard: temporarily replacing
    /// the <c>inserted.Contains(fact.Id)</c> provenance check in <c>FactJournal.Replay</c> with an
    /// unconditional <c>Link(...)</c> call (relying solely on <c>Link</c>'s SQL assertion predicate,
    /// which this forgotten row also satisfies) reddens this test — confirming the provenance check,
    /// not the SQL predicate, is what this guards. Restored after confirming.
    /// </summary>
    /// <remarks>
    /// Also test 5 of D68 §7 (post-architect-ruling): the same declined link counts both
    /// <c>AlreadyPresent</c> (the body matched in pass 1) and <c>Conflicted</c> (the edge did not,
    /// in pass 2) — the two counters describe different statements about the same record and are
    /// not mutually exclusive.
    /// </remarks>
    [Fact]
    public void Replay_OfASupersessionTargetingAForgottenRow_DeclinesRatherThanFabricatingOne()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var forgottenId = SeedRawFact(
            rebuilt, "/project/gone", "states", "a forgotten belief", T0, validTo: T0.AddHours(1));

        var facts = new List<JournalFact>
        {
            new(1, "/project/gone", "note", "states", "a forgotten belief", null, null, "project", "stated",
                false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 2, "revised",
                T0.ToUnixTimeSeconds()),
            new(2, "/project/replacement", "note", "states", "what allegedly replaced it", null, null,
                "project", "stated", false, null, T0.AddMinutes(30).ToUnixTimeSeconds(), null, null, null,
                T0.AddMinutes(30).ToUnixTimeSeconds()),
        };

        var result = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(1, result.Conflicted);
        Assert.Null(SupersededByOf(rebuilt, forgottenId));
        Assert.Equal(0, SupersessionRowCount(rebuilt));
    }

    /// <summary>
    /// Test 4 of D68 §7 — the falsification for the ambiguous-address case (§4.2). Two closed rows
    /// share the tuple with no live member, so no basis exists to prefer either as the address a
    /// supersession would point at. Proved to fail: temporarily forcing <c>Existing()</c>'s
    /// <c>AddressUsable</c> to always be true (the pre-fix <c>LIMIT 1</c>, no-<c>ORDER BY</c>
    /// behaviour) flips the outcome from <c>Unresolved</c> to <c>Conflicted</c>, reddening this
    /// test. Restored after confirming. Replayed twice to prove §4.1's other half: ambiguity never
    /// suppresses presence, so no third copy is ever inserted.
    /// </summary>
    [Fact]
    public void Replay_OfASupersessionTargetingAnAllClosedAmbiguousPair_ReportsUnresolvedAndInsertsNoThirdCopy()
    {
        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var firstClosed = SeedRawFact(
            rebuilt, "/project/dup2", "states", "an ambiguous closed belief", T0, validTo: T0.AddHours(1));
        var secondClosed = SeedRawFact(
            rebuilt, "/project/dup2", "states", "an ambiguous closed belief", T0, validTo: T0.AddHours(2));

        var facts = new List<JournalFact>
        {
            new(1, "/project/dup2", "note", "states", "an ambiguous closed belief", null, null, "project",
                "stated", false, null, T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), 2, "revised",
                T0.ToUnixTimeSeconds()),
            new(2, "/project/other2", "note", "states", "an unrelated replacement", null, null, "project",
                "stated", false, null, T0.AddMinutes(10).ToUnixTimeSeconds(), null, null, null,
                T0.AddMinutes(10).ToUnixTimeSeconds()),
        };

        var first = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, first.Unresolved);
        Assert.Null(SupersededByOf(rebuilt, firstClosed));
        Assert.Null(SupersededByOf(rebuilt, secondClosed));
        Assert.Equal(2, FactRowCountFor(rebuilt, "/project/dup2", "an ambiguous closed belief"));

        var second = FactJournal.Replay(rebuilt, facts, apply: true);

        Assert.Equal(1, second.Unresolved);
        Assert.Equal(0, second.Written);
        Assert.Equal(2, FactRowCountFor(rebuilt, "/project/dup2", "an ambiguous closed belief"));
    }
}
