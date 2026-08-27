using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// Every schema version bump ships with a test that opens a database written by the previous
/// version and reads it correctly. These are those tests, one downgrade fixture per version.
/// </summary>
/// <remarks>
/// A rollback-built fixture inherits every current-schema structure it does not explicitly
/// revert. A version-N fixture must be version-N-shaped for everything any migration touches —
/// v6 was the first migration to alter <c>fact</c>'s own columns, and v7 the first to add a whole
/// new table (<c>repo_enrollment</c>), both catching this the same way: every rollback-built
/// fixture below v7 must also drop that table, or the version-7 migration's unconditional
/// <c>CREATE TABLE</c> fails against a fixture that silently still has it.
/// Extend these drops when a future migration adds another column or table.
/// </remarks>
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

    /// <summary>
    /// The structural half of making a downgrade fixture version-N-shaped for <c>details</c> (v6).
    /// Must run AFTER any facts are written in the same fixture: pre-v6 <c>details</c> did not exist
    /// to constrain those writes, but current-schema <see cref="FactStore.InsertFact"/> still writes
    /// to it, so dropping the column first would fail the insert rather than the migration.
    /// </summary>
    private static void DropDetailsColumn(SqliteConnection connection) =>
        Execute(connection, "ALTER TABLE fact DROP COLUMN details;");

    /// <summary>
    /// The structural half of making a downgrade fixture version-N-shaped (N &lt; 8) for
    /// <c>repo_registry.last_scan_suppressed_reason</c> — every fixture below version 8 needs
    /// this now that the column lives on <c>repo_registry</c> rather than <c>repo_enrollment</c>
    /// (docs/repo-index-remediation-spec.md §14), the same way <see cref="DropDetailsColumn"/>
    /// covers <c>fact.details</c> for version 6.
    /// </summary>
    private static void DropSuppressionColumn(SqliteConnection connection) =>
        Execute(connection, "ALTER TABLE repo_registry DROP COLUMN last_scan_suppressed_reason;");

    /// <summary>
    /// The structural half of making a downgrade fixture version-N-shaped (N &lt; 14) for
    /// <c>fact.analyzer_tier</c> — every fixture below version 14 needs this the same way
    /// <see cref="DropSuppressionColumn"/> covers version 8's column, so the version-14
    /// migration's unconditional <c>ALTER TABLE ADD COLUMN</c> cannot silently no-op against a
    /// fixture that still has it (D60).
    /// </summary>
    private static void DropAnalyzerTierColumn(SqliteConnection connection) =>
        Execute(connection, "ALTER TABLE fact DROP COLUMN analyzer_tier;");

    /// <summary>
    /// The structural half of making a downgrade fixture version-N-shaped (N &lt; 9) for the
    /// cross-machine sync side tables — every fixture below version 9 needs this the same way
    /// <see cref="DropSuppressionColumn"/> covers version 8's column, so the version-9
    /// migration's unconditional <c>CREATE TABLE</c> cannot silently no-op against a fixture
    /// that still has them (D60).
    /// </summary>
    private static void DropSyncTables(SqliteConnection connection)
    {
        Execute(connection, "DROP TABLE sync_chunk_state;");
        Execute(connection, "DROP TABLE sync_deferred_close;");
    }

    /// <summary>
    /// The structural half of making a downgrade fixture version-N-shaped (N &lt; 10) for the
    /// conflict-verdicts side table — every fixture below version 10 needs this the same way
    /// <see cref="DropSyncTables"/> covers version 9's tables, so the version-10 migration's
    /// unconditional <c>CREATE TABLE</c> cannot silently no-op against a fixture that still
    /// has it (D60).
    /// </summary>
    private static void DropFactRelationTable(SqliteConnection connection)
    {
        Execute(connection, "DROP TABLE fact_relation;");
    }

    /// <summary>
    /// The structural half of making a downgrade fixture version-N-shaped (N &lt; 11) for the
    /// per-fact always-sync opt-in (docs/memory-expansion/01-sync-spec.md, "Per-fact opt-in") —
    /// every fixture below version 11 needs this the same way <see cref="DropFactRelationTable"/>
    /// covers version 10's table, so the version-11 migration's unconditional <c>CREATE TABLE</c>
    /// cannot silently no-op against a fixture that still has it (D60).
    /// </summary>
    private static void DropFactSyncRequestTable(SqliteConnection connection)
    {
        Execute(connection, "DROP TABLE fact_sync_request;");
    }

    /// <summary>
    /// The structural half of making a downgrade fixture version-N-shaped (N &lt; 12) for the
    /// review-due marker (docs/memory-expansion/04-lifecycle-spec.md) — every fixture below
    /// version 12 needs this the same way <see cref="DropFactSyncRequestTable"/> covers version
    /// 11's table, so the version-12 migration's unconditional <c>CREATE TABLE</c> cannot
    /// silently no-op against a fixture that still has it (D60).
    /// </summary>
    private static void DropFactReviewTable(SqliteConnection connection)
    {
        Execute(connection, "DROP TABLE fact_review;");
    }

    /// <summary>Builds a store at version 1 holding one fact, and closes it.</summary>
    private static long WriteVersion1Store(SandboxHome sandbox, string path, string body)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(connection, Version1Fts);

        var id = FactStore.Remember(
            connection,
            new FactWrite(path, "note", "states", body, "project", "stated"),
            T0).FactId;

        DropDetailsColumn(connection);
        DropSuppressionColumn(connection);
        DropSyncTables(connection);
        DropFactRelationTable(connection);
        DropFactSyncRequestTable(connection);
        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "DROP TABLE repo_enrollment;");
        Assert.Equal(1, EngramDatabase.ReadSchemaVersion(connection));
        return id;
    }

    /// <summary>
    /// Version 6 added <c>details</c> directly on <c>fact</c> — the first migration to alter that
    /// table's own columns rather than an auxiliary index or FTS structure. Every earlier fixture in
    /// this file rolls back only auxiliary state and leaves <c>fact</c> untouched, which is exactly
    /// what the version-5 shape here cannot do: <c>ALTER TABLE ... DROP COLUMN</c> is what makes the
    /// fixture GENUINELY lack the column (the D60 lesson — a fixture that only rewrites
    /// <c>schema_version</c> already has it, and the migration's <c>ADD COLUMN</c> would be a no-op
    /// that a broken migration could still pass).
    /// </summary>
    /// <remarks>
    /// The fact is written through the current <see cref="FactStore.Remember"/> API before
    /// <see cref="DropDetailsColumn"/> runs, because that column has to exist for the write to
    /// succeed — the store only becomes genuinely version-5-shaped once the column is gone.
    /// </remarks>
    private static long WriteVersion5Store(SandboxHome sandbox, string path, string body)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = FactStore.Remember(
            connection,
            new FactWrite(path, "note", "states", body, "project", "stated"),
            T0).FactId;

        DropDetailsColumn(connection);
        DropSuppressionColumn(connection);
        DropSyncTables(connection);
        DropFactRelationTable(connection);
        DropFactSyncRequestTable(connection);
        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "DROP TABLE repo_enrollment;");
        Execute(connection, "UPDATE schema_meta SET value = '5' WHERE key = 'schema_version';");
        Assert.Equal(5, EngramDatabase.ReadSchemaVersion(connection));

        return id;
    }

    /// <summary>
    /// Version 7 adds <c>repo_enrollment</c> as a brand new table — CREATE, not ALTER — so unlike
    /// <see cref="WriteVersion5Store"/>'s column revert, "version-6-shaped" means dropping the
    /// table outright rather than reverting a value. It has to exist long enough for the
    /// <c>repo_registry</c> rows below to be written first, then it goes — the same D60 ordering
    /// <see cref="WriteVersion5Store"/> uses, so a migration whose CREATE TABLE degrades to
    /// IF NOT EXISTS cannot no-op against this fixture the way it could against a rolled-back one.
    /// </summary>
    private static void WriteVersion6Store(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(
            connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0), "
            + "('/projects/acme/code/gone', 'github.com/acme/gone', NULL, 0);");

        DropSuppressionColumn(connection);
        DropSyncTables(connection);
        DropFactRelationTable(connection);
        DropFactSyncRequestTable(connection);
        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "DROP TABLE repo_enrollment;");
        Execute(connection, "UPDATE schema_meta SET value = '6' WHERE key = 'schema_version';");
        Assert.Equal(6, EngramDatabase.ReadSchemaVersion(connection));
    }

    /// <summary>
    /// Version 8 added <c>last_scan_suppressed_reason</c> as an <c>ALTER TABLE ADD COLUMN</c> on
    /// <c>repo_registry</c>, not <c>repo_enrollment</c> — moved there because the column fails
    /// silently for any indexed-but-unenrolled repo (docs/repo-index-remediation-spec.md §14) —
    /// the first migration to alter that table's own columns rather than create or drop it
    /// outright, so "version-7-shaped" means dropping just that column: the same D60 shape
    /// <see cref="WriteVersion5Store"/> uses for <c>fact.details</c>, and required here because
    /// the migration's <c>ALTER TABLE ADD COLUMN</c> is unguarded — a fixture that only stamped
    /// <c>schema_version</c> back without genuinely lacking the column would throw on re-add
    /// rather than proving anything about the migration. This fixture also needs a matching
    /// <c>repo_registry</c> row for the same identity, since <c>repo_enrollment</c>'s row alone
    /// no longer carries anything the migration or the test that follows reads.
    /// </summary>
    private static void WriteVersion7Store(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(
            connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0);");
        Execute(
            connection,
            "INSERT INTO repo_enrollment (identity, state, source, last_root, decided_at, last_full_scan_at) "
            + "VALUES ('github.com/acme/api', 'enrolled', 'user', '/tmp/api', 0, NULL);");

        DropSuppressionColumn(connection);
        DropSyncTables(connection);
        DropFactRelationTable(connection);
        DropFactSyncRequestTable(connection);
        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "UPDATE schema_meta SET value = '7' WHERE key = 'schema_version';");
        Assert.Equal(7, EngramDatabase.ReadSchemaVersion(connection));
    }

    /// <summary>
    /// Version 9 added the cross-machine sync side tables (docs/memory-expansion/01-sync-spec.md) as
    /// two brand new <c>CREATE TABLE</c>s, not an <c>ALTER</c> — the same shape as version 7's
    /// <c>repo_enrollment</c> (<see cref="WriteVersion6Store"/>): "version-8-shaped" means the
    /// tables are outright absent, dropped after the current-schema store is opened so the
    /// migration's unconditional <c>CREATE TABLE</c> cannot silently no-op against a fixture that
    /// still has them (D60).
    /// </summary>
    private static void WriteVersion8Store(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(
            connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0);");

        DropSyncTables(connection);
        DropFactRelationTable(connection);
        DropFactSyncRequestTable(connection);
        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "UPDATE schema_meta SET value = '8' WHERE key = 'schema_version';");
        Assert.Equal(8, EngramDatabase.ReadSchemaVersion(connection));
    }

    /// <summary>
    /// Version 10 added <c>fact_relation</c> as a brand new <c>CREATE TABLE</c>, the same shape
    /// as version 9's sync tables (<see cref="WriteVersion8Store"/>): "version-9-shaped" means the
    /// sync tables are present (they exist as of version 9) but <c>fact_relation</c> is outright
    /// absent, dropped after the current-schema store is opened so the migration's unconditional
    /// <c>CREATE TABLE</c> cannot silently no-op against a fixture that still has it (D60).
    /// </summary>
    private static void WriteVersion9Store(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(
            connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0);");

        DropFactRelationTable(connection);
        DropFactSyncRequestTable(connection);
        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "UPDATE schema_meta SET value = '9' WHERE key = 'schema_version';");
        Assert.Equal(9, EngramDatabase.ReadSchemaVersion(connection));
    }

    /// <summary>
    /// Version 11 added <c>fact_sync_request</c> as a brand new <c>CREATE TABLE</c>, the same shape
    /// as version 10's <c>fact_relation</c> (<see cref="WriteVersion9Store"/>): "version-10-shaped"
    /// means <c>fact_relation</c> is present (it exists as of version 10) but
    /// <c>fact_sync_request</c> is outright absent, dropped after the current-schema store is
    /// opened so the migration's unconditional <c>CREATE TABLE</c> cannot silently no-op against a
    /// fixture that still has it (D60).
    /// </summary>
    private static void WriteVersion10Store(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(
            connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0);");

        DropFactSyncRequestTable(connection);
        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "UPDATE schema_meta SET value = '10' WHERE key = 'schema_version';");
        Assert.Equal(10, EngramDatabase.ReadSchemaVersion(connection));
    }

    /// <summary>
    /// Version 12 added <c>fact_review</c> as a brand new <c>CREATE TABLE</c>, the same shape
    /// as version 11's <c>fact_sync_request</c> (<see cref="WriteVersion10Store"/>): "version-11-shaped"
    /// means <c>fact_sync_request</c> is present (it exists as of version 11) but
    /// <c>fact_review</c> is outright absent, dropped after the current-schema store is
    /// opened so the migration's unconditional <c>CREATE TABLE</c> cannot silently no-op against a
    /// fixture that still has it (D60).
    /// </summary>
    private static void WriteVersion11Store(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Execute(
            connection,
            "INSERT INTO repo_registry (repo_path, identity, disk_path, created_at) VALUES "
            + "('/projects/acme/code/api', 'github.com/acme/api', '/tmp/api', 0);");

        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "UPDATE schema_meta SET value = '11' WHERE key = 'schema_version';");
        Assert.Equal(11, EngramDatabase.ReadSchemaVersion(connection));
    }

    /// <summary>
    /// Builds a store at version 12: <c>ux_fact_edge_live</c> does not exist yet, and
    /// <c>ux_fact_live</c> is its pre-v13 combined form (no <c>object_id IS NULL</c> clause).
    /// </summary>
    private static void WriteVersion12Store(SandboxHome sandbox)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Execute(connection, "DROP INDEX ux_fact_edge_live;");
        Execute(connection, "DROP INDEX ux_fact_live;");
        Execute(
            connection,
            "CREATE UNIQUE INDEX ux_fact_live ON fact(subject_id, predicate) WHERE valid_to IS NULL;");

        DropAnalyzerTierColumn(connection);
        Execute(connection, "UPDATE schema_meta SET value = '12' WHERE key = 'schema_version';");
        Assert.Equal(12, EngramDatabase.ReadSchemaVersion(connection));
    }

    /// <summary>
    /// Version 14 added <c>fact.analyzer_tier</c> as an <c>ALTER TABLE ADD COLUMN</c> — the same
    /// shape as version 8's <c>last_scan_suppressed_reason</c> (<see cref="WriteVersion7Store"/>),
    /// so "version-13-shaped" means dropping just that column: the D60 shape, required here
    /// because the migration's <c>ALTER TABLE ADD COLUMN</c> is unguarded and a fixture that only
    /// stamped <c>schema_version</c> back without genuinely lacking the column would throw on
    /// re-add rather than proving anything about the migration (code-navigation Phase 4 spec §9
    /// item 3).
    /// </summary>
    private static long WriteVersion13Store(SandboxHome sandbox, string path, string body)
    {
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var id = FactStore.Remember(
            connection,
            new FactWrite(path, "note", "states", body, "project", "stated"),
            T0).FactId;

        Execute(connection, "ALTER TABLE fact DROP COLUMN analyzer_tier;");
        Execute(connection, "UPDATE schema_meta SET value = '13' WHERE key = 'schema_version';");
        Assert.Equal(13, EngramDatabase.ReadSchemaVersion(connection));

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

        DropDetailsColumn(connection);
        DropSuppressionColumn(connection);
        DropSyncTables(connection);
        DropFactRelationTable(connection);
        DropFactSyncRequestTable(connection);
        DropFactReviewTable(connection);
        DropAnalyzerTierColumn(connection);
        Execute(connection, "DROP TABLE repo_enrollment;");
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

    /// <summary>Phase 2 acceptance item 1: a genuine v12 fixture store migrates to v13.</summary>
    [Fact]
    public void Opening_AVersion12Store_MigratesToV13WithBothIndexesAndTheThreadIndexIntact()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion12Store(sandbox);

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));
        Assert.Equal(1L, IndexPartial(reopened, "ux_fact_live"));
        Assert.Equal(1L, IndexPartial(reopened, "ux_fact_edge_live"));
        Assert.Equal("subject_id,predicate partial=0", ThreadIndexShape(reopened));
    }

    private static long IndexPartial(SqliteConnection connection, string indexName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT partial FROM pragma_index_list('fact') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", indexName);
        var value = command.ExecuteScalar();
        Assert.NotNull(value);
        return (long)value!;
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

    /// <summary>
    /// The migration adds capacity, never rewrites belief content (D8) — the pre-existing fact's
    /// body must survive byte-identical, and its new <c>details</c> column reads NULL rather than
    /// some backfilled value, because splitting an existing body is exactly what the rule forbids.
    /// </summary>
    [Fact]
    public void Migrating_AVersion5Store_AddsTheDetailsColumnWithoutTouchingExistingFacts()
    {
        using var sandbox = new SandboxHome(initialize: false);
        const string Body = "It binds loopback only.";
        var id = WriteVersion5Store(sandbox, "/knowledge/testing/kestrel", Body);
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        using (var command = reopened.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('fact') WHERE name = 'details';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        var fact = FactStore.ReadById(reopened, id);
        Assert.NotNull(fact);
        Assert.Equal(Body, fact.Body);
        Assert.Null(fact.Details);

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guard 8.3-8: the migration must create repo_enrollment unconditionally and backfill only
    /// registered repos with a live disk_path — a detached repo_registry row backfilling to
    /// 'enrolled' would silently re-consent a repo whose checkout is already gone. Falsified by
    /// temporarily dropping the version 7 migration's <c>WHERE disk_path IS NOT NULL</c> clause
    /// on the backfill INSERT; confirmed red (the detached 'github.com/acme/gone' row backfilled
    /// too), then restored.
    /// </summary>
    [Fact]
    public void Migrating_AVersion6Store_CreatesRepoEnrollmentAndBackfillsOnlyLiveRegistrations()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion6Store(sandbox);
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'repo_enrollment';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT state, source, last_root, last_full_scan_at FROM repo_enrollment "
                + "WHERE identity = 'github.com/acme/api';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("enrolled", reader.GetString(0));
            Assert.Equal("backfill", reader.GetString(1));
            Assert.Equal("/tmp/api", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));
        }

        using (var command = reopened.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM repo_enrollment WHERE identity = 'github.com/acme/gone';";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Falsified per §14.5.1 by temporarily removing the migration's <c>if (from &lt; 8)</c> block
    /// so no <c>ALTER TABLE</c> runs: confirmed red — <c>OpenInitialized</c> throws
    /// <c>InvalidOperationException</c> because <c>ReadSchemaVersion</c> never advances past 7 while
    /// <see cref="EngramDatabase.SchemaVersion"/> is 8 — then restored.
    /// </summary>
    [Fact]
    public void Migrating_AVersion7Store_AddsTheSuppressionColumnWithoutTouchingExistingRows()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion7Store(sandbox);
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('repo_registry') "
                + "WHERE name = 'last_scan_suppressed_reason';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT state, source, last_root FROM repo_enrollment "
                + "WHERE identity = 'github.com/acme/api';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("enrolled", reader.GetString(0));
            Assert.Equal("user", reader.GetString(1));
            Assert.Equal("/tmp/api", reader.GetString(2));
        }

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT last_scan_suppressed_reason FROM repo_registry "
                + "WHERE identity = 'github.com/acme/api';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
        }

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Falsified by temporarily removing the migration's <c>if (from &lt; 9)</c> block so no
    /// <c>CREATE TABLE</c> runs: confirmed red — <c>OpenInitialized</c> throws
    /// <c>InvalidOperationException</c> because <c>ReadSchemaVersion</c> never advances past 8 while
    /// <see cref="EngramDatabase.SchemaVersion"/> is 9 — then restored.
    /// </summary>
    [Fact]
    public void Migrating_AVersion8Store_CreatesTheSyncTablesWithoutTouchingExistingRows()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion8Store(sandbox);
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' "
                + "AND name IN ('sync_chunk_state', 'sync_deferred_close');";
            Assert.Equal(2L, (long)command.ExecuteScalar()!);
        }

        using (var command = reopened.CreateCommand())
        {
            command.CommandText = "SELECT identity FROM repo_registry WHERE identity = 'github.com/acme/api';";
            Assert.Equal("github.com/acme/api", (string)command.ExecuteScalar()!);
        }

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Falsified by temporarily removing the migration's <c>if (from &lt; 10)</c> block so no
    /// <c>CREATE TABLE</c> runs: confirmed red — <c>OpenInitialized</c> throws
    /// <c>InvalidOperationException</c> because <c>ReadSchemaVersion</c> never advances past 9 while
    /// <see cref="EngramDatabase.SchemaVersion"/> is 10 — then restored.
    /// </summary>
    [Fact]
    public void Migrating_AVersion9Store_CreatesTheFactRelationTableWithoutTouchingExistingRows()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion9Store(sandbox);
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'fact_relation';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        using (var command = reopened.CreateCommand())
        {
            command.CommandText = "SELECT identity FROM repo_registry WHERE identity = 'github.com/acme/api';";
            Assert.Equal("github.com/acme/api", (string)command.ExecuteScalar()!);
        }

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Falsified by temporarily removing the migration's <c>if (from &lt; 11)</c> block so no
    /// <c>CREATE TABLE</c> runs: confirmed red — <c>OpenInitialized</c> throws
    /// <c>InvalidOperationException</c> ("Database schema version 10 does not match the 11 this
    /// binary was built for. Refusing to open it rather than reading it wrongly.") because
    /// <c>ReadSchemaVersion</c> never advances past 10 while
    /// <see cref="EngramDatabase.SchemaVersion"/> is 11 — then restored.
    /// </summary>
    [Fact]
    public void Migrating_AVersion10Store_CreatesTheFactSyncRequestTableWithoutTouchingExistingRows()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion10Store(sandbox);
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'fact_sync_request';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        using (var command = reopened.CreateCommand())
        {
            command.CommandText = "SELECT identity FROM repo_registry WHERE identity = 'github.com/acme/api';";
            Assert.Equal("github.com/acme/api", (string)command.ExecuteScalar()!);
        }

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Falsified by temporarily removing the migration's <c>if (from &lt; 12)</c> block so no
    /// <c>CREATE TABLE</c> runs: confirmed red — <c>OpenInitialized</c> throws
    /// <c>InvalidOperationException</c> because <c>ReadSchemaVersion</c> never advances past 11 while
    /// <see cref="EngramDatabase.SchemaVersion"/> is 12 — then restored.
    /// </summary>
    [Fact]
    public void Migrating_AVersion11Store_CreatesTheFactReviewTableWithoutTouchingExistingRows()
    {
        using var sandbox = new SandboxHome(initialize: false);
        WriteVersion11Store(sandbox);
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        using (var command = reopened.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'fact_review';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        using (var command = reopened.CreateCommand())
        {
            command.CommandText = "SELECT identity FROM repo_registry WHERE identity = 'github.com/acme/api';";
            Assert.Equal("github.com/acme/api", (string)command.ExecuteScalar()!);
        }

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Items 1 and 2 (code-navigation Phase 4 spec §9): the migration applies (schema_version
    /// reaches v14, not merely a column's presence — falsify by leaving <c>SchemaVersion</c> at
    /// 13, which reddens because <c>ReadSchemaVersion</c> never advances) and the pre-existing
    /// fact survives with <c>analyzer_tier IS NULL</c>, body and validity untouched.
    /// </summary>
    /// <remarks>
    /// Falsified per §9 item 3 by temporarily removing the migration's <c>if (from &lt; 14)</c>
    /// block so no <c>ALTER TABLE</c> runs: confirmed red — <c>OpenInitialized</c> throws
    /// <c>InvalidOperationException</c> because <c>ReadSchemaVersion</c> never advances past 13
    /// while <see cref="EngramDatabase.SchemaVersion"/> is 14 — then restored.
    /// </remarks>
    [Fact]
    public void Migrating_AVersion13Store_AddsAnalyzerTierColumnWithoutTouchingExistingFacts()
    {
        using var sandbox = new SandboxHome(initialize: false);
        const string Body = "It binds loopback only.";
        var id = WriteVersion13Store(sandbox, "/knowledge/testing/kestrel", Body);
        SqliteConnection.ClearAllPools();

        using var reopened = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.Equal(EngramDatabase.SchemaVersion, EngramDatabase.ReadSchemaVersion(reopened));

        using (var command = reopened.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('fact') WHERE name = 'analyzer_tier';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        var fact = FactStore.ReadById(reopened, id);
        Assert.NotNull(fact);
        Assert.Equal(Body, fact.Body);
        Assert.Null(fact.ValidTo);
        Assert.Null(fact.AnalyzerTier);

        var snapshot = Assert.Single(BackupStore.List(sandbox.Home));
        Assert.Contains("pre-v" + EngramDatabase.SchemaVersion, snapshot.Name, StringComparison.Ordinal);
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

    /// <summary>
    /// Version 5 added <c>ix_fact_thread</c>, which the fresh path creates from
    /// <c>docs/engram-schema.sql</c> and the migration path from a C# string — two spellings of one
    /// index, the same drift hazard <see cref="AMigratedStore_HasTheSameTokenIndexDdlAsAFreshOne"/>
    /// covers for <c>fact_token</c>.
    /// </summary>
    /// <remarks>
    /// <para>Asserted as a concrete shape rather than only fresh-equals-migrated, because two stores
    /// that both lack the index compare equal — the assertion that would pass with the whole change
    /// reverted. <c>partial=0</c> is the load-bearing field: <c>ux_fact_live</c> already indexes
    /// these two columns and is useless here precisely because it is partial on
    /// <c>valid_to IS NULL</c>, while a thread length counts closed facts too (D57).</para>
    ///
    /// <para>The <c>DROP INDEX</c> is what makes this a migration test at all, and it is not
    /// bookkeeping. <see cref="WriteVersion1Store"/> builds its "old" store by opening a
    /// <i>current</i>-schema one and rolling <c>schema_version</c> back, so <c>ix_fact_thread</c> is
    /// already present and version 5's <c>CREATE INDEX IF NOT EXISTS</c> no-ops. Measured by
    /// flipping the migration to create a partial index with this line absent: 18 of 18 green. The
    /// version 4 step is immune to the same trap only because it is a <c>DROP</c> and
    /// <c>CREATE</c> pair that runs unconditionally.</para>
    /// </remarks>
    [Fact]
    public void AMigratedStore_HasTheSameThreadIndexAsAFreshOne()
    {
        using var migratedHome = new SandboxHome(initialize: false);
        using var freshHome = new SandboxHome(initialize: false);
        WriteVersion1Store(migratedHome, "/knowledge/testing/kestrel", "It binds loopback only.");
        using (var pre = EngramDatabase.Open(migratedHome.Home))
        {
            Execute(pre, "DROP INDEX ix_fact_thread;");
        }

        using var migrated = EngramDatabase.OpenInitialized(migratedHome.Home);
        using var fresh = EngramDatabase.OpenInitialized(freshHome.Home);

        Assert.Equal("subject_id,predicate partial=0", ThreadIndexShape(fresh));
        Assert.Equal(ThreadIndexShape(fresh), ThreadIndexShape(migrated));
    }

    /// <summary>
    /// Version 16 added <c>ix_fact_object</c>, serving the reverse-edge (<c>callers</c>) lookup
    /// that previously scanned <c>fact</c> once per candidate (fact-object-index-migration.md §1).
    /// </summary>
    /// <remarks>
    /// <para>Compared by shape (<c>pragma_index_list</c>/<c>pragma_index_info</c>), not by
    /// <c>sqlite_master</c> DDL text: <c>docs/engram-schema.sql</c>'s copy and the migration's
    /// <c>CREATE INDEX IF NOT EXISTS</c> copy legitimately differ in text for the same logical
    /// index, unlike the trigger case where byte-identical text was the right check (§5.2).
    /// <c>partial=1</c> is the field that differs from <see cref="ThreadIndexShape"/>'s
    /// <c>partial=0</c> and is what catches a dropped <c>WHERE</c> clause.</para>
    ///
    /// <para>The <c>DROP INDEX</c> is the D60 fix already applied to the v5 test above:
    /// <see cref="WriteVersion1Store"/> rolls a current-schema store back, so it does not remove
    /// indexes, and a broken v16 migration would otherwise no-op against a fixture that already
    /// has the index.</para>
    ///
    /// <para>The shape assertion alone cannot tell a correct predicate from a wrong one that
    /// happens to still be partial, so the query-plan assertion is required alongside it — it is
    /// the only check that fails when the predicate SQLite sees does not imply the index's and the
    /// lookup silently degrades back to a scan (§5.3).</para>
    /// </remarks>
    [Fact]
    public void AMigratedStore_HasTheSameObjectIndexAsAFreshOne()
    {
        using var migratedHome = new SandboxHome(initialize: false);
        using var freshHome = new SandboxHome(initialize: false);
        WriteVersion1Store(migratedHome, "/knowledge/testing/kestrel", "It binds loopback only.");
        using (var pre = EngramDatabase.Open(migratedHome.Home))
        {
            Execute(pre, "DROP INDEX ix_fact_object;");
        }

        using var migrated = EngramDatabase.OpenInitialized(migratedHome.Home);
        using var fresh = EngramDatabase.OpenInitialized(freshHome.Home);

        Assert.Equal("object_id,predicate partial=1", ObjectIndexShape(fresh));
        Assert.Equal(ObjectIndexShape(fresh), ObjectIndexShape(migrated));
        Assert.Contains("USING INDEX ix_fact_object", CallersQueryPlan(migrated));
        Assert.Contains("USING INDEX ix_fact_object", ImplementersQueryPlan(migrated));
    }

    private static string ObjectIndexShape(SqliteConnection connection)
    {
        using var list = connection.CreateCommand();
        list.CommandText = "SELECT partial FROM pragma_index_list('fact') WHERE name = 'ix_fact_object';";
        if (list.ExecuteScalar() is not long partial)
        {
            return "(absent)";
        }

        using var info = connection.CreateCommand();
        info.CommandText = "SELECT group_concat(name) FROM (SELECT name FROM pragma_index_info('ix_fact_object'));";
        return $"{info.ExecuteScalar()} partial={partial}";
    }

    /// <summary>
    /// The exact WHERE clause <see cref="CodeCallGraph"/>'s <c>callers</c> lookup runs, so this
    /// proves the predicate SQLite actually sees is usable — <c>pragma_index_list.partial</c>
    /// reports whether an index is partial but never what its predicate says, so a shape match
    /// alone cannot catch a predicate SQLite declines to match (§5.3).
    /// </summary>
    private static string CallersQueryPlan(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "EXPLAIN QUERY PLAN SELECT f.path, o.name, f.analyzer_tier FROM fact f JOIN entity o ON o.id = f.object_id "
                + "WHERE f.predicate = 'calls' AND f.valid_to IS NULL AND f.object_id IS NOT NULL AND o.path IN ($o0);";
        command.Parameters.AddWithValue("$o0", "/anything");

        var lines = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return string.Join(" | ", lines);
    }

    /// <summary>
    /// The exact WHERE clause <c>EngramMcpTools.NavigateImplementers</c> runs (the windowed
    /// reverse-edge query 5d4fb33 collapsed the old per-predicate loop into) — same reasoning as
    /// <see cref="CallersQueryPlan"/>: the shape check alone cannot tell whether this query's
    /// predicate is actually usable.
    /// </summary>
    private static string ImplementersQueryPlan(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "EXPLAIN QUERY PLAN WITH ranked AS ("
                + "SELECT f.path AS subject_path, f.predicate AS predicate, "
                + "ROW_NUMBER() OVER (PARTITION BY f.predicate ORDER BY f.predicate, f.path) AS rn "
                + "FROM fact f JOIN entity o ON o.id = f.object_id "
                + "WHERE f.predicate IN ($p0) AND f.valid_to IS NULL AND f.object_id IS NOT NULL "
                + "AND o.path IN ($o0)"
                + ") SELECT subject_path, predicate, rn FROM ranked WHERE rn <= 1001;";
        command.Parameters.AddWithValue("$p0", "implements");
        command.Parameters.AddWithValue("$o0", "/anything");

        var lines = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return string.Join(" | ", lines);
    }

    private static string ThreadIndexShape(SqliteConnection connection)
    {
        using var list = connection.CreateCommand();
        list.CommandText = "SELECT partial FROM pragma_index_list('fact') WHERE name = 'ix_fact_thread';";
        if (list.ExecuteScalar() is not long partial)
        {
            return "(absent)";
        }

        using var info = connection.CreateCommand();
        info.CommandText = "SELECT group_concat(name) FROM (SELECT name FROM pragma_index_info('ix_fact_thread'));";
        return $"{info.ExecuteScalar()} partial={partial}";
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
