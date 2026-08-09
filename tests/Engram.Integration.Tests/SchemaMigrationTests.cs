using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Every schema version bump ships with a test that opens a database written by the previous
/// version and reads it correctly. These are those tests, one downgrade fixture per version.
/// </summary>
[Collection(SqlitePoolCollection.Name)]
public class SchemaMigrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The version 1 lexical index: <c>body</c> and <c>predicate</c>, no <c>path</c>, and no
    /// re-index trigger. Written out rather than kept as a fixture file because a binary fixture
    /// is a thing nobody can read in a diff.
    /// </summary>
    private const string Version1Fts =
        """
        DROP TRIGGER fact_fts_insert;
        DROP TRIGGER fact_fts_close;
        DROP TRIGGER fact_fts_delete;
        DROP TRIGGER fact_fts_repath;
        DROP TABLE fact_fts;

        CREATE VIRTUAL TABLE fact_fts USING fts5(
          body,
          predicate,
          content='fact',
          content_rowid='id',
          tokenize='porter unicode61'
        );

        CREATE TRIGGER fact_fts_insert AFTER INSERT ON fact BEGIN
          INSERT INTO fact_fts(rowid, body, predicate)
            VALUES (new.id, new.body, new.predicate);
        END;

        CREATE TRIGGER fact_fts_close AFTER UPDATE OF valid_to ON fact
          WHEN old.valid_to IS NULL AND new.valid_to IS NOT NULL BEGIN
          INSERT INTO fact_fts(fact_fts, rowid, body, predicate)
            VALUES ('delete', old.id, old.body, old.predicate);
        END;

        CREATE TRIGGER fact_fts_delete AFTER DELETE ON fact BEGIN
          INSERT INTO fact_fts(fact_fts, rowid, body, predicate)
            VALUES ('delete', old.id, old.body, old.predicate);
        END;

        UPDATE schema_meta SET value = '1' WHERE key = 'schema_version';
        """;

    /// <summary>
    /// The version 2 shape of the delete trigger: unconditional, so it re-deletes an entry
    /// <c>fact_fts_close</c> already removed, and FTS5 answers the second 'delete' with
    /// "database disk image is malformed".
    /// </summary>
    private const string Version2Fts =
        """
        DROP TRIGGER fact_fts_delete;

        CREATE TRIGGER fact_fts_delete AFTER DELETE ON fact BEGIN
          INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path)
            VALUES ('delete', old.id, old.body, old.predicate, old.path);
        END;

        UPDATE schema_meta SET value = '2' WHERE key = 'schema_version';
        """;

    /// <summary>Builds a store at version 1 holding one fact, and closes it.</summary>
    private static long WriteVersion1Store(SandboxHome sandbox, string path, string body)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(connection, Version1Fts);

        var id = FactStore.Remember(
            connection,
            new FactWrite(path, "note", "states", body, "project", "stated"),
            T0).FactId;

        Assert.Equal(1, EngramDatabase.ReadSchemaVersion(connection));
        return id;
    }

    /// <summary>Builds a store at version 2 holding one live fact and one closed one.</summary>
    private static (long Live, long Closed) WriteVersion2Store(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(connection, Version2Fts);

        var live = FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/kestrel", "note", "states", "It binds loopback only.", "project", "stated"),
            T0).FactId;
        var closed = FactStore.Remember(
            connection,
            new FactWrite("/knowledge/testing/nginx", "note", "states", "It fronts the kestrel.", "project", "stated"),
            T0).FactId;
        Execute(connection, $"UPDATE fact SET valid_to = '{T0:O}' WHERE id = {closed};");

        Assert.Equal(2, EngramDatabase.ReadSchemaVersion(connection));
        return (live, closed);
    }

    [Fact]
    public void Opening_AVersion1Store_MigratesItToTheCurrentVersion()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion1Store(sandbox, "/knowledge/testing/kestrel", "It binds loopback only.");

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));
    }

    /// <summary>
    /// The facts have to survive it. A migration that rebuilds a derived index and loses a belief
    /// is not a migration, and D8 forbids the class outright.
    /// </summary>
    [Fact]
    public void Migrating_LeavesEveryFactExactlyAsItWas()
    {
        using var sandbox = new SandboxHome(initialize: false);
        const string Body = "It binds loopback only.";
        var id = WriteVersion1Store(sandbox, "/knowledge/testing/kestrel", Body);

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        var fact = FactStore.ReadById(reopened, id);

        Assert.NotNull(fact);
        Assert.Equal(Body, fact.Body);
        Assert.Equal("/knowledge/testing/kestrel", fact.SubjectPath);
        Assert.Equal("stated", fact.LearnedVia);
        Assert.Null(fact.ValidTo);
    }

    [Fact]
    public void Migrating_RebuildsTheLexicalIndexOverEveryLiveFact()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var id = WriteVersion1Store(sandbox, "/knowledge/testing/kestrel", "It binds loopback only.");

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(id, Assert.Single(FactStore.SearchRanked(reopened, "loopback", 10)).FactId);
    }

    /// <summary>The point of the version bump: the subject becomes searchable, and stemmed.</summary>
    [Theory]
    [InlineData("kestrel")]
    [InlineData("kestrels")]
    public void Migrating_MakesTheSubjectPathSearchable(string query)
    {
        using var sandbox = new SandboxHome(initialize: false);
        var id = WriteVersion1Store(sandbox, "/knowledge/testing/kestrel", "It binds loopback only.");

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(id, Assert.Single(FactStore.SearchRanked(reopened, query, 10)).FactId);
    }

    /// <summary>
    /// The migration carries its own copy of the FTS5 DDL, because an FTS5 table's columns cannot
    /// be altered in place and parsing the statements back out of the schema file would fail
    /// silently on a reformatted comment. This is what makes the duplicate safe: a migrated store
    /// and a fresh one must be indistinguishable.
    /// </summary>
    [Fact]
    public void AMigratedStore_HasTheSameLexicalIndexAsAFreshOne()
    {
        using var migratedHome = new SandboxHome(initialize: false);
        using var freshHome = new SandboxHome(initialize: false);
        WriteVersion1Store(migratedHome, "/knowledge/testing/kestrel", "It binds loopback only.");

        using var migrated = EngramDatabase.OpenInitialized(migratedHome.Home);
        using var fresh = EngramDatabase.OpenInitialized(freshHome.Home);

        Assert.Equal(LexicalDdl(fresh), LexicalDdl(migrated));
    }

    /// <summary>
    /// A migration is the one thing Engram's own code does that rewrites structure, unattended, on
    /// open, before anyone has decided today is a good day for it. It takes a snapshot first.
    /// </summary>
    [Fact]
    public void Migrating_SnapshotsTheStoreBeforeTouchingIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion1Store(sandbox, "/knowledge/testing/kestrel", "It binds loopback only.");
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the snapshot has to predate the migration, not merely accompany it. A copy taken after
    /// the rebuild would restore you to the state you were trying to get away from.
    /// </summary>
    [Fact]
    public void TheSnapshotAMigrationTakes_StillHoldsTheOldSchema()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion1Store(sandbox, "/knowledge/testing/kestrel", "It binds loopback only.");
        SqliteConnection.ClearAllPools();

        using (var reopened = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));
        }

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        SqliteConnection.ClearAllPools();
        using var fromSnapshot = EngramDatabase.Open(snapshot.Path);

        Assert.Equal(1, EngramDatabase.ReadSchemaVersion(fromSnapshot));
        Assert.Empty(FactStore.SearchRanked(fromSnapshot, "kestrel", 10));
    }

    [Fact]
    public void Opening_AStoreAlreadyAtTheCurrentVersion_SnapshotsNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            FactStore.Remember(
                connection,
                new FactWrite("/knowledge/testing/kestrel", "note", "states", "It binds loopback only.", "project", "stated"),
                T0);
        }

        SqliteConnection.ClearAllPools();
        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Empty(BackupStore.List(sandbox.Home));
    }

    [Fact]
    public void Opening_AStoreFromTheFuture_IsRefusedRatherThanGuessedAt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Execute(connection, "UPDATE schema_meta SET value = '9999' WHERE key = 'schema_version';");
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        });

        Assert.Contains("9999", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Version 3 exists for one statement: DELETE of a closed fact. Closing already removed it
    /// from the index, the version 2 trigger deleted it a second time, and FTS5 fails the whole
    /// statement with "database disk image is malformed" — so on version 2, `compact` would have
    /// broken on its first prune.
    /// </summary>
    [Fact]
    public void Migrating_AVersion2Store_MakesAClosedFactDeletable()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var (live, closed) = WriteVersion2Store(sandbox);

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        Execute(reopened, $"DELETE FROM fact WHERE id = {closed};");

        Assert.Equal(live, Assert.Single(FactStore.SearchRanked(reopened, "loopback", 10)).FactId);
    }

    [Fact]
    public void AStoreMigratedFromVersion2_HasTheSameLexicalIndexAsAFreshOne()
    {
        using var migratedHome = new SandboxHome(initialize: false);
        using var freshHome = new SandboxHome(initialize: false);
        WriteVersion2Store(migratedHome);

        using var migrated = EngramDatabase.OpenInitialized(migratedHome.Home);
        using var fresh = EngramDatabase.OpenInitialized(freshHome.Home);

        Assert.Equal(LexicalDdl(fresh), LexicalDdl(migrated));
    }

    /// <summary>
    /// Version 4 added <c>fact_token</c>. A store built at version 1 predates it entirely, so
    /// migrating one is the one path that actually exercises the CREATE — every other test in
    /// this file builds its "old" fixture by taking a current-schema store and rolling
    /// schema_version back, which already has the table.
    /// </summary>
    [Fact]
    public void Migrating_RebuildsTheTokenIndexOverEveryLiveFact()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var id = WriteVersion1Store(sandbox, "/knowledge/testing/kestrel", "It binds loopback only.");

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.True(FactTokenIndex.IsReady(reopened));
        Assert.Equal(
            new HashSet<string> { "kestrel", "binds", "loopback", "only" },
            TokensFor(reopened, id));
    }

    [Fact]
    public void AMigratedStore_HasTheSameTokenIndexDdlAsAFreshOne()
    {
        using var migratedHome = new SandboxHome(initialize: false);
        using var freshHome = new SandboxHome(initialize: false);
        WriteVersion1Store(migratedHome, "/knowledge/testing/kestrel", "It binds loopback only.");

        using var migrated = EngramDatabase.OpenInitialized(migratedHome.Home);
        using var fresh = EngramDatabase.OpenInitialized(freshHome.Home);

        Assert.Equal(TokenIndexDdl(fresh), TokenIndexDdl(migrated));
    }

    private static List<string> LexicalDdl(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE name LIKE 'fact_fts%' AND sql IS NOT NULL ORDER BY name;";

        var statements = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            statements.Add(reader.GetString(0));
        }

        return statements;
    }

    private static List<string> TokenIndexDdl(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE name LIKE 'fact_token%' AND sql IS NOT NULL ORDER BY name;";

        var statements = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            statements.Add(reader.GetString(0));
        }

        return statements;
    }

    private static HashSet<string> TokensFor(SqliteConnection connection, long factId)
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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
