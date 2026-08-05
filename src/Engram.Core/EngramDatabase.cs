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
    public const int SchemaVersion = 1;

    public const int BusyTimeoutMilliseconds = 5000;

    // The schema doc is embedded rather than copied, so docs/engram-schema.sql stays the
    // single authority for database shape instead of drifting from a duplicate in source.
    // The logical name deliberately avoids the file's own stem: it would put the substring
    // the hardcoded-path lint scans for into a string that is not a path at all.
    private const string SchemaResourceName = "Engram.Core.Schema.sql";

    /// <summary>Opens a configured connection. Does not create or verify the schema.</summary>
    public static SqliteConnection Open(EngramHome home) => Open(home.DatabasePath);

    /// <summary>Opens a configured connection, creating the schema if the file is new.</summary>
    public static SqliteConnection OpenInitialized(EngramHome home)
    {
        var connection = Open(home);
        try
        {
            EnsureSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static SqliteConnection Open(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        try
        {
            Configure(connection);
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

    public static void EnsureSchema(SqliteConnection connection)
    {
        if (TableExists(connection, "schema_meta"))
        {
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

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);

        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
