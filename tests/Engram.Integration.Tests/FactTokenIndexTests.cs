using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// <c>fact_token</c> against real stores: does the incrementally-maintained table agree with a
/// from-scratch recomputation after every write site that is supposed to touch it, and is a
/// forgotten call site actually caught.
/// </summary>
[Collection(SqlitePoolCollection.Name)]
public class FactTokenIndexTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshStore_TokenIndexIsAlreadyReady()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.True(FactTokenIndex.IsReady(connection));
        Assert.Equal(FactTokenIndexState.Ready, FactTokenIndex.ReadState(connection));
    }

    [Fact]
    public void EnsureBuilt_RebuildsAStaleIndex_AndTheResultIsCurrent()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = Write(connection, "/projects/acme/notes", "the trunk workflow ships releases");

        // A version behind is what a store from an older build of this binary would carry —
        // distinct from unbuilt, and doctor and repair both have to tell the two apart.
        Stamp(connection, "0");
        Assert.Equal(FactTokenIndexState.VersionMismatch, FactTokenIndex.ReadState(connection));

        FactTokenIndex.EnsureBuilt(connection);

        Assert.True(FactTokenIndex.IsReady(connection));
        Assert.Contains("trunk", ReadTokensFor(connection, factId));
    }

    /// <summary>
    /// The guard the spec asks for: drive every site that is supposed to keep <c>fact_token</c>
    /// current, then compare the table's contents against what <see cref="FactTokenIndex.Rebuild"/>
    /// — a structurally independent code path reading straight from <c>fact</c> — produces for the
    /// same store. A forgotten <c>Add</c> or <c>Remove</c> anywhere in between shows up as a set
    /// difference here, not as a passing test.
    /// </summary>
    [Fact]
    public void FactToken_AgreesWithAFromScratchRecomputation_AcrossEveryWriteSite()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        // New fact (FactStore.InsertFact's Add).
        Write(connection, "/projects/acme/notes", "the zanzibar workflow ships releases");

        // Supersede it (Remember's close-hook Remove, and InsertFact's Add for the replacement).
        Write(connection, "/projects/acme/notes", "the trunk workflow ships releases now");

        // A fact that gets retracted (Forget's Remove).
        var retracted = Write(connection, "/projects/acme/temp", "a note that gets retracted later");
        FactStore.Forget(connection, retracted, "no longer relevant", T0.AddMinutes(1));

        // A session note with no custom subject: the fingerprint leaf must stay unindexed.
        SessionFacts.Append(
            connection, "session-a", "the deploy pipeline uses github actions",
            subject: null, evidence: null, agent: null, T0.AddMinutes(2));

        // A session note given a real subject after the fact: the rename must trigger a
        // Remove-then-Add refresh rather than leaving it indexed under the fingerprint default.
        SessionFacts.Append(
            connection, "session-a", "prefers dvorak over qwerty for typing",
            subject: "keyboard layout", evidence: null, agent: null, T0.AddMinutes(3));

        AssertMatchesFromScratchRebuild(connection);
    }

    /// <summary>
    /// The same guard, over <c>FactJournal.Insert</c> (live and closed) and <c>Link</c>
    /// (supersession) — the two chokepoints the direct <c>FactStore</c> writes above never touch.
    /// </summary>
    [Fact]
    public void FactToken_AgreesWithAFromScratchRecomputation_AfterAJournalReplay()
    {
        using var source = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/projects/acme/notes", "the zanzibar workflow ships releases");
            Write(connection, "/projects/acme/notes", "the trunk workflow ships releases now");
            FactJournal.Write(connection, source.Home, T0.AddMinutes(1));
        }

        var facts = FactJournal.Parse(File.ReadLines(FactJournal.PathIn(source.Home)), out var skipped);
        Assert.Equal(0, skipped);

        using var target = new SandboxHome();
        using var targetConnection = EngramDatabase.OpenInitialized(target.Home);
        var result = FactJournal.Replay(targetConnection, facts, apply: true);

        Assert.Equal(2, result.Written);

        AssertMatchesFromScratchRebuild(targetConnection);
    }

    /// <summary>
    /// The falsification target. Breaking <c>FactTokenIndex.Add</c>'s call inside
    /// <c>FactJournal.Insert</c> — the site the spec names as the one a reader is most likely to
    /// forget — turns the <c>Assert.Contains</c> calls below red; restoring it turns them green.
    /// Verified by hand in both directions (see the implementor's report).
    /// </summary>
    [Fact]
    public void FactJournalReplay_IndexesTheLiveFactItInserts()
    {
        using var source = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            Write(connection, "/projects/acme/notes", "the trunk workflow ships releases");
            FactJournal.Write(connection, source.Home, T0.AddMinutes(1));
        }

        var facts = FactJournal.Parse(File.ReadLines(FactJournal.PathIn(source.Home)), out var skipped);
        Assert.Equal(0, skipped);

        using var target = new SandboxHome();
        using var targetConnection = EngramDatabase.OpenInitialized(target.Home);
        var result = FactJournal.Replay(targetConnection, facts, apply: true);
        Assert.Equal(1, result.Written);

        var factId = FactStore.FindLiveFactId(targetConnection, null, "/projects/acme/notes", "states");
        Assert.NotNull(factId);

        var tokens = ReadTokensFor(targetConnection, factId!.Value);
        Assert.Contains("trunk", tokens);
        Assert.Contains("workflow", tokens);
    }

    /// <summary>
    /// NEEDS-EVIDENCE 7: <c>TextFor</c> reads the subject's name, not its path, so a rename must
    /// change no indexed token.
    /// </summary>
    [Fact]
    public void MoveSubtree_ChangesNoTokenContent()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = Write(connection, "/projects/acme/notes", "the trunk workflow ships releases");
        var before = ReadTokensFor(connection, factId);

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            FactStore.MoveSubtree(
                connection, transaction, "/projects/acme/notes", "/projects/acme/decisions", T0.AddMinutes(1));
            transaction.Commit();
        }

        Assert.Equal(before, ReadTokensFor(connection, factId));
        Assert.Contains("trunk", before);
    }

    /// <summary>The other half of NEEDS-EVIDENCE 7: repair's path re-derivation likewise touches nothing.</summary>
    [Fact]
    public void StoreRepairerPathFix_ChangesNoTokenContent()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var factId = Write(connection, "/projects/acme/decisions", "quarterly releases ship from trunk");
        var before = ReadTokensFor(connection, factId);

        using (var drift = connection.CreateCommand())
        {
            drift.CommandText = "UPDATE fact SET path = '/wrong/spelling' WHERE id = $id;";
            drift.Parameters.AddWithValue("$id", factId);
            drift.ExecuteNonQuery();
        }

        var report = StoreRepairer.Repair(connection, sandbox.Home, apply: true, T0.AddMinutes(1));

        Assert.Equal(1, report.PathsDrifted);
        Assert.False(report.TokenIndexNeedsRebuild);
        Assert.Equal(before, ReadTokensFor(connection, factId));
    }

    private static void AssertMatchesFromScratchRebuild(SqliteConnection connection)
    {
        var incremental = ReadAllRows(connection);
        FactTokenIndex.Rebuild(connection);
        var recomputed = ReadAllRows(connection);

        Assert.Equal(recomputed, incremental);
        Assert.NotEmpty(incremental);
    }

    private static long Write(SqliteConnection connection, string path, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "note", "states", body, "project", "stated"),
            T0).FactId;

    private static void Stamp(SqliteConnection connection, string version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE schema_meta SET value = $v WHERE key = 'fact_token_version';";
        command.Parameters.AddWithValue("$v", version);
        command.ExecuteNonQuery();
    }

    private static HashSet<string> ReadTokensFor(SqliteConnection connection, long factId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token FROM fact_token WHERE fact_id = $id;";
        command.Parameters.AddWithValue("$id", factId);

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tokens.Add(reader.GetString(0));
        }

        return tokens;
    }

    private static HashSet<(string Token, long FactId)> ReadAllRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token, fact_id FROM fact_token;";

        var rows = new HashSet<(string, long)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }
}
