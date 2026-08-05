using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2 (D9). These run against real SQLite files because the things most likely to break
/// here — a connection-scoped pragma silently defaulting off, a CHECK constraint that was
/// never actually applied — cannot fail in a unit test.
/// </summary>
public class EngramDatabaseTests
{
    // Guards the observable contract, not the pragma statement: Microsoft.Data.Sqlite turns
    // foreign keys on by itself, so deleting our line does NOT fail this (checked). What it
    // does catch is the changes that would actually cost us enforcement — a connection string
    // carrying `Foreign Keys=False`, or a move off this provider.
    [Fact]
    public void Open_FreshConnection_HasForeignKeysOn()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.Open(sandbox.Home);

        Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys;"));
    }

    // This one has real teeth. A raw connection reports 0, so an open path that stops setting
    // it fails here — and would otherwise turn every concurrent write into an instant
    // SQLITE_BUSY instead of a wait.
    [Fact]
    public void Open_FreshConnection_HasBusyTimeoutSet()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.Open(sandbox.Home);

        Assert.Equal((long)EngramDatabase.BusyTimeoutMilliseconds, Scalar(connection, "PRAGMA busy_timeout;"));
    }

    // Likewise: the default is 2 (FULL). NORMAL is the setting WAL is worth having — an fsync
    // per commit is most of what WAL was adopted to avoid.
    [Fact]
    public void Open_FreshConnection_HasSynchronousNormal()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.Open(sandbox.Home);

        Assert.Equal(1L, Scalar(connection, "PRAGMA synchronous;"));
    }

    [Fact]
    public void OpenInitialized_LeavesDatabaseInWalMode()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;") as string);
    }

    [Fact]
    public void OpenInitialized_RecordsTheSchemaVersionTheBinaryExpects()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(connection));
    }

    [Fact]
    public void OpenInitialized_OnAnAlreadyInitialisedFile_IsANoOp()
    {
        using var sandbox = new SandboxHome();

        using (var first = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            InsertEntity(first, "/people/jim");
        }

        using var second = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(1L, Scalar(second, "SELECT COUNT(*) FROM entity;"));
    }

    // The schema file names this test specifically: a dangling reference must be rejected
    // through the real open path, not merely through a connection someone remembered to
    // configure. It also proves the constraint exists at all, which the pragma read alone
    // would not — a table declared without the REFERENCES clause passes that and fails this.
    [Fact]
    public void DanglingForeignKey_IsRejectedThroughTheRealOpenPath()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        var entityId = InsertEntity(connection, "/people/jim");
        InsertFact(connection, entityId, learnedVia: "stated");

        var error = Assert.Throws<SqliteException>(() =>
            Execute(connection, "UPDATE fact SET superseded_by = 999 WHERE id = 1;"));

        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // D19 allows exactly three tiers. 'indexed' and 'derived' are the two values the earlier
    // schema carried and that D19/D23 removed — 'indexed' because it described regenerability
    // rather than grounding, 'derived' because it collided with D8's meaning of the word.
    [Theory]
    [InlineData("indexed")]
    [InlineData("derived")]
    [InlineData("guessed")]
    public void Fact_RejectsProvenanceOutsideTheThreeTiers(string learnedVia)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var entityId = InsertEntity(connection, "/people/jim");

        Assert.Throws<SqliteException>(() => InsertFact(connection, entityId, learnedVia));
    }

    [Theory]
    [InlineData("stated")]
    [InlineData("observed")]
    [InlineData("inferred")]
    public void Fact_AcceptsEachOfTheThreeTiers(string learnedVia)
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var entityId = InsertEntity(connection, $"/people/{learnedVia}");

        InsertFact(connection, entityId, learnedVia);

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM fact;"));
    }

    // D23: a fact is regenerable or it is not. Anything else would let a third state creep
    // in beside the boolean that repair keys off.
    [Fact]
    public void Fact_RejectsRegenerableOutsideZeroOrOne()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var entityId = InsertEntity(connection, "/people/jim");

        Assert.Throws<SqliteException>(() => InsertFact(connection, entityId, "observed", regenerable: 2));
    }

    // The safe default matters more than it looks: a fact written without an opinion must
    // never be one repair is allowed to delete.
    [Fact]
    public void Fact_DefaultsToNotRegenerable()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var entityId = InsertEntity(connection, "/people/jim");

        Execute(
            connection,
            $"""
            INSERT INTO fact (subject_id, predicate, body, path, scope, learned_via, valid_from, created_at)
            VALUES ({entityId}, 'prefers', 'A body.', '/people/jim', 'user', 'stated', 1, 1);
            """);

        Assert.Equal(0L, Scalar(connection, "SELECT regenerable FROM fact;"));
    }

    [Fact]
    public void BeginWrite_CommitsWhatItWrote()
    {
        using var sandbox = new SandboxHome();
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        using (var transaction = EngramDatabase.BeginWrite(connection))
        {
            InsertEntity(connection, "/people/jim", transaction);
            transaction.Commit();
        }

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM entity;"));
    }

    [Fact]
    public void ReadSchemaSql_IsEmbeddedAndCarriesTheFactTable()
    {
        var sql = EngramDatabase.ReadSchemaSql();

        Assert.Contains("CREATE TABLE fact", sql, StringComparison.Ordinal);
        Assert.Contains("regenerable", sql, StringComparison.Ordinal);
    }

    private static long InsertEntity(SqliteConnection connection, string path, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO entity (path, kind, name, created_at) VALUES ($path, 'person', 'jim', 1);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$path", path);

        return (long)command.ExecuteScalar()!;
    }

    private static void InsertFact(SqliteConnection connection, long entityId, string learnedVia, int regenerable = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO fact (subject_id, predicate, body, path, scope, learned_via, regenerable, valid_from, created_at)
            VALUES ($subject, 'prefers', 'A body.', '/people/jim', 'user', $learnedVia, $regenerable, 1, 1);
            """;
        command.Parameters.AddWithValue("$subject", entityId);
        command.Parameters.AddWithValue("$learnedVia", learnedVia);
        command.Parameters.AddWithValue("$regenerable", regenerable);

        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
