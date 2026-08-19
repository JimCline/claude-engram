using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// <c>sync_requests.jsonl</c> against real stores: whether an always-sync flag written from one
/// store's fact ids can be traced into a different store's, through the same journal-id map D32
/// built for <c>facts.jsonl</c> in the same replay.
/// </summary>
public sealed class SyncRequestJournalTests
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

    private static IReadOnlyList<JournalSyncRequest> RereadSyncRequests(EngramHome home)
    {
        var requests = SyncRequestJournal.Parse(File.ReadLines(SyncRequestJournal.PathIn(home)), out var skipped);
        Assert.Equal(0, skipped);
        return requests;
    }

    private static IReadOnlyList<long> FlaggedFactIds(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT fact_id FROM fact_sync_request ORDER BY fact_id;";
        using var reader = command.ExecuteReader();

        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    [Fact]
    public void Write_PutsEverySyncRequestOnItsOwnLine()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var a = Write(connection, "/project/a", "the first thing", T0);

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            FactSyncRequests.Insert(connection, transaction, a, T0.ToUnixTimeSeconds());
            transaction.Commit();
        }

        var written = SyncRequestJournal.Write(connection, sandbox.Home, T0);

        Assert.Equal(1, written);
        var lines = File.ReadAllLines(SyncRequestJournal.PathIn(sandbox.Home));
        Assert.Equal(2, lines.Length);
        Assert.Contains("engram-sync-requests", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_ResolvesTheFlaggedFactThroughTheSameIdMapFactReplayBuilt()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            var a = Write(connection, "/project/a", "the first thing", T0);
            Write(connection, "/project/b", "the second thing", T0.AddMinutes(1));

            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                FactSyncRequests.Insert(connection, transaction, a, T0.ToUnixTimeSeconds());
                transaction.Commit();
            }

            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
            SyncRequestJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        var facts = RereadFacts(source.Home);
        var requests = RereadSyncRequests(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        // The target's fact ids are guaranteed to differ from the source's own — replaying
        // facts into an empty store still starts numbering at 1, same as the source did, so a
        // deliberately different first write forces the ids apart and makes a same-id
        // coincidence not the reason the resolution below passes.
        Write(rebuilt, "/project/decoy", "not part of this replay", T0);

        var factResult = FactJournal.Replay(rebuilt, facts, apply: true, out var idMap);
        Assert.Equal(2, factResult.Written);

        var requestResult = SyncRequestJournal.Replay(rebuilt, requests, facts, idMap, apply: true);

        Assert.Equal(1, requestResult.Written);
        Assert.Equal(0, requestResult.Unresolved);
        var flaggedId = Assert.Single(FlaggedFactIds(rebuilt));
        Assert.Equal(idMap.Single(kv => facts.Single(f => f.Id == kv.Key).Subject == "/project/a").Value, flaggedId);
    }

    /// <summary>
    /// Falsified by removing the <c>factId is null</c> guard in
    /// <see cref="SyncRequestJournal.Replay"/> so an unresolved row falls through to an insert:
    /// confirmed red — the call throws (there is no fact id to insert against) rather than
    /// silently attaching the flag to an arbitrary fact — then restored.
    /// </summary>
    [Fact]
    public void Replay_SkipsAndCountsASyncRequestWhoseFactIsNotInTheFactsBeingReplayed()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            // The journal is taken here, before the flagged fact exists — standing in for a
            // sync request whose fact lives in a different journal slice than the one being
            // replayed.
            FactJournal.Write(connection, source.Home, T0.AddMinutes(1));
        }

        var facts = Array.Empty<JournalFact>();

        var requests = new[]
        {
            new JournalSyncRequest(
                FactSubject: "/project/outsider",
                FactPredicate: "states",
                FactBody: "never journalled",
                FactValidFrom: T0.ToUnixTimeSeconds(),
                RequestedAt: T0.ToUnixTimeSeconds()),
        };

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var idMap = new Dictionary<long, long>();
        var result = SyncRequestJournal.Replay(rebuilt, requests, facts, idMap, apply: true);

        Assert.Equal(0, result.Written);
        Assert.Equal(1, result.Unresolved);
        Assert.Empty(FlaggedFactIds(rebuilt));
    }

    [Fact]
    public void Replay_TwiceInARow_TheSecondCountsAlreadyPresentRatherThanDuplicating()
    {
        using var source = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            var a = Write(connection, "/project/a", "the first thing", T0);

            using (var transaction = EngramDatabase.BeginWrite(connection))
            {
                FactSyncRequests.Insert(connection, transaction, a, T0.ToUnixTimeSeconds());
                transaction.Commit();
            }

            FactJournal.Write(connection, source.Home, T0.AddMinutes(2));
            SyncRequestJournal.Write(connection, source.Home, T0.AddMinutes(2));
        }

        var facts = RereadFacts(source.Home);
        var requests = RereadSyncRequests(source.Home);

        using var target = new SandboxHome(initialize: false);
        using var rebuilt = EngramDatabase.OpenInitialized(target.Home);

        var factResult = FactJournal.Replay(rebuilt, facts, apply: true, out var idMap);
        SyncRequestJournal.Replay(rebuilt, requests, facts, idMap, apply: true);

        var secondFactResult = FactJournal.Replay(rebuilt, facts, apply: true, out var secondIdMap);
        Assert.Equal(1, secondFactResult.AlreadyPresent);
        var second = SyncRequestJournal.Replay(rebuilt, requests, facts, secondIdMap, apply: true);

        Assert.Equal(0, second.Written);
        Assert.Equal(1, second.AlreadyPresent);
        Assert.Single(FlaggedFactIds(rebuilt));
    }

    /// <summary>
    /// The round trip the spec's "Durability for <c>fact_sync_request</c>" subsection asks for
    /// directly: flag a fact, journal it, drop the live table entirely, and confirm the journal
    /// alone restores the flag against the fact's own (unchanged, same-store) id.
    /// </summary>
    [Fact]
    public void Write_ThenReplay_RestoresAFlagDroppedFromTheLiveTable()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var a = Write(connection, "/project/a", "the first thing", T0);

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            FactSyncRequests.Insert(connection, transaction, a, T0.ToUnixTimeSeconds());
            transaction.Commit();
        }

        SyncRequestJournal.Write(connection, sandbox.Home, T0);
        var requests = RereadSyncRequests(sandbox.Home);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM fact_sync_request;";
            command.ExecuteNonQuery();
        }

        Assert.Empty(FlaggedFactIds(connection));

        var facts = FactJournal.Read(connection).ToList();
        var idMap = facts.ToDictionary(f => f.Id, f => f.Id);

        var result = SyncRequestJournal.Replay(connection, requests, facts, idMap, apply: true);

        Assert.Equal(1, result.Written);
        Assert.Equal([a], FlaggedFactIds(connection));
    }
}
