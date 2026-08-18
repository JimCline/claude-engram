using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// <c>relations.jsonl</c> against real stores: whether a verdict written from one store's fact
/// ids can be traced into a different store's, through the same journal-id map D32 built for
/// <c>facts.jsonl</c> in the same replay.
/// </summary>
public sealed class RelationJournalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static long Write(SqliteConnection connection, string path, string body, DateTimeOffset at) =>
        FactStore.Remember(connection, new FactWrite(path, "note", "states", body, "project", "stated"), at).FactId;

    private static IReadOnlyList<JournalFact> RereadFacts(EngramHome home)
    {
        var facts = FactJournal.Parse(File.ReadLines(FactJournal.PathIn(home)), out var skipped);
        Assert.Equal(0, skipped);
        return facts;
    }

    private static IReadOnlyList<JournalRelation> RereadRelations(EngramHome home)
    {
        var relations = RelationJournal.Parse(File.ReadLines(RelationJournal.PathIn(home)), out var skipped);
        Assert.Equal(0, skipped);
        return relations;
    }

    private static IReadOnlyList<string> Relations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT relation FROM fact_relation ORDER BY id;";
        using var reader = command.ExecuteReader();

        var relations = new List<string>();
        while (reader.Read())
        {
            relations.Add(reader.GetString(0));
        }

        return relations;
    }

    [Fact]
    public void Write_PutsEveryRelationOnItsOwnLine()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var a = Write(connection, "/project/a", "the first thing", T0);
        var b = Write(connection, "/project/b", "the second thing", T0);

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            FactRelations.Judge(connection, transaction, a, b, "conflicts_with", "disagree on scope", T0.ToUnixTimeSeconds());
            transaction.Commit();
        }

        var written = RelationJournal.Write(connection, sandbox.Home, T0);

        Assert.Equal(1, written);
        var lines = File.ReadAllLines(RelationJournal.PathIn(sandbox.Home));
        Assert.Equal(2, lines.Length);
        Assert.Contains("engram-relations", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_ResolvesBothSidesThroughTheSameIdMapFactReplayBuilt()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            var a = Write(connection, "/project/a", "the first thing", T0);
            var b = Write(connection, "/project/b", "the second thing", T0.AddMinutes(1));

            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                FactRelations.Judge(connection, transaction, a, b, "supersedes", "restated more precisely", T0.ToUnixTimeSeconds());
                transaction.Commit();
            }

            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
            RelationJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        var facts = RereadFacts(source.Home);
        var relations = RereadRelations(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        // The target's fact ids are guaranteed to differ from the source's own — replaying
        // facts into an empty store still starts numbering at 1, same as the source did, so a
        // deliberately different first write forces the ids apart and makes a same-side-id
        // coincidence not the reason the resolution below passes.
        Write(rebuilt, "/project/decoy", "not part of this replay", T0);

        var factResult = FactJournal.Replay(rebuilt, facts, apply: true, out var idMap);
        Assert.Equal(2, factResult.Written);

        var relationResult = RelationJournal.Replay(rebuilt, relations, facts, idMap, apply: true);

        Assert.Equal(1, relationResult.Written);
        Assert.Equal(0, relationResult.Unresolved);
        Assert.Equal(["supersedes"], Relations(rebuilt));
    }

    [Fact]
    public void Replay_SkipsAndCountsARelationWhoseSideIsNotInTheFactsBeingReplayed()
    {
        using var source = new SandboxHome(initialize: false);
        long a, outsider;
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            a = Write(connection, "/project/a", "the first thing", T0);

            // The journal is taken here, before `outsider` exists — standing in for a relation
            // whose other side lives in a different journal slice than the one being replayed.
            FactJournal.Write(connection, source.Home, T0.AddMinutes(1));

            outsider = Write(connection, "/project/outsider", "never journalled", T0);
            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                FactRelations.Judge(connection, transaction, a, outsider, "not_conflict", "different scopes", T0.ToUnixTimeSeconds());
                transaction.Commit();
            }
        }

        var facts = RereadFacts(source.Home);
        Assert.Single(facts);

        var relations = new[]
        {
            new JournalRelation(
                FactSubject: "/project/a", FactPredicate: "states", FactBody: "the first thing", FactValidFrom: T0.ToUnixTimeSeconds(),
                RelatedSubject: "/project/outsider", RelatedPredicate: "states", RelatedBody: "never journalled", RelatedValidFrom: T0.ToUnixTimeSeconds(),
                Relation: "not_conflict", Reason: "different scopes", JudgedAt: T0.ToUnixTimeSeconds()),
        };

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var factResult = FactJournal.Replay(rebuilt, facts, apply: true, out var idMap);
        Assert.Equal(1, factResult.Written);

        var relationResult = RelationJournal.Replay(rebuilt, relations, facts, idMap, apply: true);

        Assert.Equal(0, relationResult.Written);
        Assert.Equal(1, relationResult.Unresolved);
        Assert.Empty(Relations(rebuilt));
    }

    [Fact]
    public void Replay_TwiceInARow_TheSecondCountsAlreadyPresentRatherThanDuplicating()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            var a = Write(connection, "/project/a", "the first thing", T0);
            var b = Write(connection, "/project/b", "the second thing", T0.AddMinutes(1));

            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                FactRelations.Judge(connection, transaction, a, b, "scoped", "not comparable", T0.ToUnixTimeSeconds());
                transaction.Commit();
            }

            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
            RelationJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        var facts = RereadFacts(source.Home);
        var relations = RereadRelations(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var factResult = FactJournal.Replay(rebuilt, facts, apply: true, out var idMap);
        RelationJournal.Replay(rebuilt, relations, facts, idMap, apply: true);

        var secondFactResult = FactJournal.Replay(rebuilt, facts, apply: true, out var secondIdMap);
        Assert.Equal(2, secondFactResult.AlreadyPresent);
        var second = RelationJournal.Replay(rebuilt, relations, facts, secondIdMap, apply: true);

        Assert.Equal(0, second.Written);
        Assert.Equal(1, second.AlreadyPresent);
        Assert.Single(Relations(rebuilt));
    }
}
