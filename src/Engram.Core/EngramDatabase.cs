using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The one routine every component opens the database through.
/// </summary>
/// <remarks>
/// It exists because these pragmas are connection-scoped. Declaring them in the schema file
/// configures the connection that applied the schema and nothing else, so a component opening
/// its own connection gets none of them. Routing every open through here is what makes the
/// schema's settings real (D4).
/// <para>
/// Measured, so the reasoning is not folklore: a raw Microsoft.Data.Sqlite connection reads
/// back <c>foreign_keys=1</c>, <c>busy_timeout=0</c>, <c>synchronous=2</c>. The provider
/// already turns foreign keys on, so that line is insurance against a connection string or
/// provider change rather than the thing enforcing them today. The timeout and the durability
/// setting are the two this routine genuinely supplies.
/// </para>
/// </remarks>
public static class EngramDatabase
{
    public const int SchemaVersion = 12;

    public const int BusyTimeoutMilliseconds = 5000;

    // The schema doc is embedded rather than copied, so docs/engram-schema.sql stays the
    // single authority for database shape instead of drifting from a duplicate in source.
    // The logical name deliberately avoids the file's own stem: it would put the substring
    // the hardcoded-path lint scans for into a string that is not a path at all.
    private const string SchemaResourceName = "Engram.Core.Schema.sql";

    /// <summary>Opens a configured connection. Does not create or verify the schema.</summary>
    public static SqliteConnection Open(EngramHome home) => Open(home.DatabasePath, home.LibDir);

    /// <summary>Opens a configured connection, creating the schema if the file is new.</summary>
    public static SqliteConnection OpenInitialized(EngramHome home)
    {
        var connection = Open(home);
        try
        {
            EnsureSchema(connection, home);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string ConnectionStringFor(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

    /// <summary>Closes pooled connections to one database, so its file can be moved or deleted.</summary>
    /// <remarks>
    /// <para>Disposing a <see cref="SqliteConnection"/> returns its handle to a pool rather than
    /// closing it, so the file stays open afterwards. On Unix that is invisible, because an open
    /// file can still be unlinked. On Windows it is an <c>IOException</c> on every attempt to remove
    /// the directory — measured as 346 Windows CI failures, all of them one line of test cleanup and
    /// none of them a defect in what was being tested.</para>
    ///
    /// <para><b>Targeted, never <c>SqliteConnection.ClearAllPools</c>.</b> That method is exactly as
    /// wide as its name and disposes handles the pool has <i>already handed out</i>, so the code it
    /// breaks is whatever is between renting a connection and using it — measured in this suite as an
    /// <c>ObjectDisposedException</c> thrown inside an unrelated test's initializer. Pools are keyed
    /// by connection string, so clearing only this database's leaves every other one alone.</para>
    ///
    /// <para>The string is built here rather than by the caller for the same reason: a pool is
    /// identified by that exact text, so a caller that reconstructed it would keep working until
    /// <see cref="Open(string, string?)"/> gained an option, and then release nothing while
    /// reporting success.</para>
    /// </remarks>
    public static void ReleasePooledConnections(string databasePath)
    {
        using var handle = new SqliteConnection(ConnectionStringFor(databasePath));
        SqliteConnection.ClearPool(handle);
    }

    /// <param name="libraryDirectory">
    /// Where to look for <c>sqlite-vec</c>. Omitting it opens a connection that cannot answer a
    /// vector query — correct for a caller with no home to resolve one from, and a trap for any
    /// other caller, because connection pooling can make the missing extension look present.
    /// Prefer <see cref="Open(EngramHome)"/>, which supplies it.
    /// </param>
    public static SqliteConnection Open(string databasePath, string? libraryDirectory = null)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(ConnectionStringFor(databasePath));
        connection.Open();

        try
        {
            Configure(connection);

            // Best-effort and deliberately unexamined here: an instance without embeddings is
            // the ordinary case, not a fault. VectorExtension explains why this cannot be
            // deferred to the callers that query vectors.
            VectorExtension.Load(connection, libraryDirectory);

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Begins a write transaction. Always <c>BEGIN IMMEDIATE</c>: a deferred transaction that
    /// upgrades to a writer raises <c>SQLITE_BUSY_SNAPSHOT</c>, which <c>busy_timeout</c>
    /// cannot wait out, so the retry that would rescue an ordinary busy error does not apply
    /// (D4). Taking the write lock up front is what makes the timeout meaningful.
    /// </summary>
    public static SqliteTransaction BeginWrite(SqliteConnection connection) =>
        connection.BeginTransaction(deferred: false);

    public static void EnsureSchema(SqliteConnection connection) => EnsureSchema(connection, home: null);

    /// <param name="home">
    /// Where to put the snapshot taken before a migration runs. Omitting it migrates without one,
    /// which is correct only for a caller that has no home to write into — a test opening a bare
    /// file, say. <see cref="OpenInitialized(EngramHome)"/> supplies it.
    /// </param>
    public static void EnsureSchema(SqliteConnection connection, EngramHome? home)
    {
        if (TableExists(connection, "schema_meta"))
        {
            var from = ReadSchemaVersion(connection);
            if (from < SchemaVersion && home is not null)
            {
                // The one moment Engram's own code puts authored truth at risk. Everything else
                // that writes is append-only; a migration rewrites structure, and it does so
                // unattended, on open, before anyone has decided today is a good day for it.
                BackupStore.Take(connection, home, DateTimeOffset.UtcNow, $"pre-v{SchemaVersion}");
            }

            Migrate(connection, from);
            VerifySchemaVersion(connection);
            return;
        }

        // Applied outside a transaction on purpose: the schema sets journal_mode, and SQLite
        // refuses to change journal mode from within one.
        Execute(connection, ReadSchemaSql());
        VerifySchemaVersion(connection);
    }

    public static string ReadSchemaSql()
    {
        using var stream = typeof(EngramDatabase).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded schema '{SchemaResourceName}' is missing from the assembly.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        return command.ExecuteScalar() as string;
    }

    public static void WriteMeta(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO schema_meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);

        command.ExecuteNonQuery();
    }

    public static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_meta WHERE key = 'schema_version';";

        var raw = command.ExecuteScalar() as string;
        if (!int.TryParse(raw, out var version))
        {
            throw new InvalidOperationException(
                $"schema_meta holds no readable schema_version (found '{raw ?? "<null>"}').");
        }

        return version;
    }

    private static void Configure(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
    }

    private static void VerifySchemaVersion(SqliteConnection connection)
    {
        var version = ReadSchemaVersion(connection);
        if (version != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {version} does not match the {SchemaVersion} this binary "
                    + "was built for. Refusing to open it rather than reading it wrongly.");
        }
    }

    /// <summary>
    /// Brings an older store forward, one version at a time.
    /// </summary>
    /// <remarks>
    /// <para>Bounded by D8 rather than by convention: a migration may only touch state that can
    /// be regenerated from what is already stored. Every step below rebuilds a derived index. The
    /// day one needs to alter a fact body, a validity window, or a supersession row, it is not a
    /// migration — it is a rewrite of authored truth, and the answer is a new fact.</para>
    ///
    /// <para>Version 6 adds a nullable column rather than rebuilding an index, and that still
    /// qualifies under D8: adding a nullable column leaves every existing row's belief content
    /// byte-identical; it adds capacity for future authored truth without rewriting any. The day
    /// a migration backfills or splits an existing body, THAT is the alteration the rule
    /// forbids.</para>
    ///
    /// <para>Forward only, and no version is skipped, so each step can assume exactly the shape
    /// the one before it left.</para>
    /// </remarks>
    private static void Migrate(SqliteConnection connection, int from)
    {
        if (from > SchemaVersion)
        {
            // An older binary against a newer store. Nothing to do here — the verify below is
            // what refuses it, and refusing is right: this code cannot know what changed.
            return;
        }

        if (from < 3)
        {
            // Version 2 added path to fact_fts; version 3 gave fact_fts_delete its WHEN
            // clause — a closed fact is already out of the index via fact_fts_close, and
            // FTS5 answers the second 'delete' with "database disk image is malformed".
            // Both changes are index shape only, so one rebuild lands the current form
            // from either starting point.
            RebuildFactFts(connection);
            WriteMeta(connection, null, "schema_version", "3");
        }

        if (from < 4)
        {
            // Version 4 adds fact_token, the literal-token overlap lane. DROP IF EXISTS first: the
            // table is derived state under D8 — it holds no authored belief, only a recomputation
            // of fact and entity, so dropping and rebuilding it destroys nothing. That is also why
            // this is safe to do unconditionally rather than reason about which starting shape the
            // store had: a downgrade fixture that already has the table from a fresher schema
            // converges to the current one either way.
            Execute(
                connection,
                null,
                """
                DROP TABLE IF EXISTS fact_token;

                CREATE TABLE fact_token (
                  token   TEXT    NOT NULL,
                  fact_id INTEGER NOT NULL REFERENCES fact(id) ON DELETE CASCADE,
                  PRIMARY KEY (token, fact_id)
                ) WITHOUT ROWID;

                CREATE INDEX ix_fact_token_fact ON fact_token(fact_id);
                """);

            FactTokenIndex.Rebuild(connection);
            WriteMeta(connection, null, "schema_version", "4");
        }

        if (from < 5)
        {
            // Version 5 adds ix_fact_thread, which is pure query planning: it creates no state, so
            // unlike version 4 there is nothing to reconcile with a store that already has it, and
            // IF NOT EXISTS covers a downgrade fixture that does.
            Execute(connection, null, "CREATE INDEX IF NOT EXISTS ix_fact_thread ON fact(subject_id, predicate);");
            WriteMeta(connection, null, "schema_version", "5");
        }

        if (from < 6)
        {
            Execute(connection, null, "ALTER TABLE fact ADD COLUMN details TEXT;");
            WriteMeta(connection, null, "schema_version", "6");
        }

        if (from < 7)
        {
            // repo_enrollment is authored truth (D8) — the user's yes/no/later decision — kept
            // out of repo_registry because StoreCompactor deletes registry rows under a path
            // prefix. Backfilling every already-registered repo to enrolled narrows nothing:
            // today's --auto already indexes any git checkout gated on nothing else. Every
            // backfilled row gets last_full_scan_at = NULL, which is due, forcing one full scan
            // per repo on its next session start — the one-shot repair of defect (a).
            //
            // This backfill emits no telemetry (§6.2/§6.10): a store with thousands of already-
            // registered repos would otherwise write that many `enrollment` records on the first
            // open after upgrade, none of them a real decision, and corrupt D18/D43's adoption
            // counts with rows nobody made.
            Execute(
                connection,
                null,
                """
                CREATE TABLE repo_enrollment (
                  identity          TEXT PRIMARY KEY,
                  state             TEXT NOT NULL CHECK (state IN ('enrolled','declined','deferred')),
                  source            TEXT NOT NULL CHECK (source IN ('user','backfill')),
                  last_root         TEXT,
                  decided_at        INTEGER NOT NULL,
                  last_full_scan_at INTEGER
                );

                CREATE INDEX ix_repo_enrollment_root ON repo_enrollment(last_root);
                """);

            Execute(
                connection,
                null,
                """
                INSERT INTO repo_enrollment (identity, state, source, last_root, decided_at, last_full_scan_at)
                SELECT identity, 'enrolled', 'backfill', disk_path, created_at, NULL
                  FROM repo_registry
                 WHERE disk_path IS NOT NULL;
                """);

            WriteMeta(connection, null, "schema_version", "7");
        }

        if (from < 8)
        {
            // Unguarded: a store that already has the column throws loudly rather than silently
            // no-opping, which is the acceptable failure mode here (docs/repo-index-remediation-spec.md
            // §14.5.1) — the trap that guard would reintroduce is a fixture that only stamps the
            // version down without actually lacking the column.
            Execute(
                connection,
                null,
                """
                ALTER TABLE repo_registry ADD COLUMN last_scan_suppressed_reason TEXT
                  CHECK (last_scan_suppressed_reason IN ('truncated', 'empty-scan'));
                """);

            WriteMeta(connection, null, "schema_version", "8");
        }

        if (from < 9)
        {
            // Cross-machine sync side tables (docs/memory-expansion/01-sync-spec.md) — nothing
            // added to `fact`. Both are derived in the weak sense (D8): rebuildable by
            // re-running `sync import` over the full chunk history.
            Execute(
                connection,
                null,
                """
                CREATE TABLE sync_chunk_state (
                  machine_id TEXT NOT NULL,
                  seq        INTEGER NOT NULL,
                  applied_at INTEGER NOT NULL,
                  fact_count INTEGER NOT NULL,
                  close_count INTEGER NOT NULL,
                  PRIMARY KEY (machine_id, seq)
                );

                CREATE TABLE sync_deferred_close (
                  subject_path TEXT NOT NULL,
                  predicate    TEXT NOT NULL,
                  body         TEXT NOT NULL,
                  valid_from   INTEGER NOT NULL,
                  valid_to     INTEGER NOT NULL,
                  superseded_by_body TEXT,
                  superseded_by_valid_from INTEGER,
                  status TEXT NOT NULL DEFAULT 'deferred' CHECK (status IN ('deferred','stalled')),
                  retry_count INTEGER NOT NULL DEFAULT 0,
                  first_seen_at INTEGER NOT NULL,
                  source_chunk TEXT NOT NULL,
                  PRIMARY KEY (subject_path, predicate, body, valid_from)
                );
                """);

            WriteMeta(connection, null, "schema_version", "9");
        }

        if (from < 10)
        {
            // Conflict verdicts (docs/memory-expansion/02-conflict-verdicts-spec.md) —
            // nothing added to `fact` (D8). A verdict is an annotation, never a fact mutation.
            Execute(
                connection,
                null,
                """
                CREATE TABLE fact_relation (
                  id INTEGER PRIMARY KEY,
                  fact_id    INTEGER NOT NULL REFERENCES fact(id),
                  related_id INTEGER NOT NULL REFERENCES fact(id),
                  relation   TEXT NOT NULL CHECK (relation IN
                             ('supersedes','conflicts_with','scoped','not_conflict')),
                  reason     TEXT,
                  judged_at  INTEGER NOT NULL
                );
                CREATE INDEX ix_fact_relation_fact    ON fact_relation(fact_id);
                CREATE INDEX ix_fact_relation_related ON fact_relation(related_id);
                """);

            WriteMeta(connection, null, "schema_version", "10");
        }

        if (from < 11)
        {
            // Scoped export (docs/memory-expansion/01-sync-spec.md) — an authored decision, not
            // derived from `fact` or from the chunk history (D8's "derived state is repairable"
            // does not cover this table), so it is insert-only like fact_relation rather than a
            // column on `fact`.
            Execute(
                connection,
                null,
                """
                CREATE TABLE fact_sync_request (
                  fact_id      INTEGER NOT NULL PRIMARY KEY REFERENCES fact(id),
                  requested_at INTEGER NOT NULL
                );
                """);

            WriteMeta(connection, null, "schema_version", "11");
        }

        if (from < 12)
        {
            // Review-due marker (docs/memory-expansion/04-lifecycle-spec.md) — nothing added to
            // `fact` (D8). A reminder date is an explicit, caller-supplied side fact, not derived
            // from anything a fact's body already encodes.
            Execute(
                connection,
                null,
                """
                CREATE TABLE fact_review (
                  fact_id      INTEGER PRIMARY KEY REFERENCES fact(id),
                  review_after INTEGER NOT NULL,
                  set_at       INTEGER NOT NULL
                );
                """);

            WriteMeta(connection, null, "schema_version", "12");
        }
    }

    /// <summary>
    /// Drops the lexical index and rebuilds it in its current shape from the live facts.
    /// </summary>
    /// <remarks>
    /// <para>Schema version 2 added <c>path</c> to <c>fact_fts</c>, and an FTS5 table's columns
    /// cannot be altered in place. Dropping it is safe because it holds nothing that is not
    /// derivable: external content means every indexed value is read back from <c>fact</c>.</para>
    ///
    /// <para>The DDL is duplicated from the schema file, which is the one thing about this that
    /// is genuinely unpleasant — <c>docs/engram-schema.sql</c> is the authority for database
    /// shape, and here is a second copy of part of it. Extracting the statements from the
    /// embedded file by parsing would be worse: it would make the authority a string-matching
    /// exercise that fails silently when someone reformats a comment. Instead the duplication is
    /// guarded, by a test asserting that a migrated store and a freshly created one have
    /// byte-identical <c>sqlite_master</c> entries for this table and its triggers.</para>
    /// </remarks>
    internal static void RebuildFactFts(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        Execute(
            connection,
            transaction,
            """
            DROP TRIGGER IF EXISTS fact_fts_insert;
            DROP TRIGGER IF EXISTS fact_fts_close;
            DROP TRIGGER IF EXISTS fact_fts_delete;
            DROP TRIGGER IF EXISTS fact_fts_repath;
            DROP TABLE IF EXISTS fact_fts;

            CREATE VIRTUAL TABLE fact_fts USING fts5(
              body,
              predicate,
              path,
              content='fact',
              content_rowid='id',
              tokenize='porter unicode61'
            );

            CREATE TRIGGER fact_fts_insert AFTER INSERT ON fact BEGIN
              INSERT INTO fact_fts(rowid, body, predicate, path)
                VALUES (new.id, new.body, new.predicate, new.path);
            END;

            CREATE TRIGGER fact_fts_close AFTER UPDATE OF valid_to ON fact
              WHEN old.valid_to IS NULL AND new.valid_to IS NOT NULL BEGIN
              INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path)
                VALUES ('delete', old.id, old.body, old.predicate, old.path);
            END;

            CREATE TRIGGER fact_fts_delete AFTER DELETE ON fact
              WHEN old.valid_to IS NULL BEGIN
              INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path)
                VALUES ('delete', old.id, old.body, old.predicate, old.path);
            END;

            CREATE TRIGGER fact_fts_repath AFTER UPDATE OF path ON fact
              WHEN new.valid_to IS NULL AND old.path <> new.path BEGIN
              INSERT INTO fact_fts(fact_fts, rowid, body, predicate, path)
                VALUES ('delete', old.id, old.body, old.predicate, old.path);
              INSERT INTO fact_fts(rowid, body, predicate, path)
                VALUES (new.id, new.body, new.predicate, new.path);
            END;

            INSERT INTO fact_fts(rowid, body, predicate, path)
              SELECT id, body, predicate, path FROM fact WHERE valid_to IS NULL;
            """);
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);

        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string sql) =>
        Execute(connection, transaction: null, sql);

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
