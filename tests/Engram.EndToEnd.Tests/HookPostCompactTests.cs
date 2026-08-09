using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// PostCompact is the harvester half of D62: it reads the digest block PreCompact's
/// instruction asked the summarizer to append, off the compact_summary field carried
/// directly on this hook's own stdin, and writes each kept item as a session fact. These
/// drive the published binary because the stdin-redirection guard the hook relies on
/// (Console.IsInputRedirected) only reads true against a real spawned process.
/// </summary>
public class HookPostCompactTests
{
    [Fact]
    public void WritesEachDigestItemAsASessionFactTaggedAsHarvested()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var summary = """
            <analysis>
            Some analysis text a real compaction would carry.
            </analysis>
            <summary>
            Session summary text goes here.

            <engram-digest v="1">
            - The user prefers commit messages under 70 characters.
            - D62 replaces the model remembering to call engram_digest.
            </engram-digest>
            </summary>
            """;

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root, Payload("e2e-post-compact", summary), "hook", "post-compact");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);

        var statements = ReadHarvestedStatements(home.Root);
        Assert.Equal(2, statements.Count);
        Assert.Contains("The user prefers commit messages under 70 characters.", statements);
        Assert.Contains("D62 replaces the model remembering to call engram_digest.", statements);
    }

    [Fact]
    public void WritesNothingWhenNoDigestBlockIsPresent()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("e2e-post-compact", "<analysis>\nOrdinary summary text, no digest block.\n</analysis>"),
            "hook", "post-compact");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Empty(ReadHarvestedStatements(home.Root));
        Assert.Empty(Records(home.Root, "post-compact"));
    }

    [Fact]
    public void WritesNothingForTheEmptyPairBlock()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var summary = """
            <summary>
            Nothing durable came out of this session.

            <engram-digest v="1">
            </engram-digest>
            </summary>
            """;

        var (exitCode, _, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("e2e-post-compact", summary), "hook", "post-compact");

        Assert.Equal(0, exitCode);
        Assert.Empty(ReadHarvestedStatements(home.Root));
    }

    // Idempotence for free (D62 2b): a compaction that harvests twice — a retried hook, a
    // stale replay — must not duplicate what it already stored, because PathFor fingerprints
    // on (session, agent, statement) and SessionFacts.Append returns the existing id for a
    // live match rather than writing again.
    [Fact]
    public void RepeatingTheSameCompactionWritesEachStatementOnce()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var summary = """
            <engram-digest v="1">
            - The user's timezone is Mountain Time.
            </engram-digest>
            """;

        EngramProcess.RunWithStdin(home.Root, Payload("e2e-post-compact", summary), "hook", "post-compact");
        var (secondExit, _, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("e2e-post-compact", summary), "hook", "post-compact");

        Assert.Equal(0, secondExit);
        Assert.Single(ReadHarvestedStatements(home.Root));
    }

    [Fact]
    public void RecordsTelemetryOnlyWhenAFactWasWritten()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var summary = """
            <engram-digest v="1">
            - A fact worth recording once.
            </engram-digest>
            """;

        EngramProcess.RunWithStdin(home.Root, Payload("e2e-post-compact", summary), "hook", "post-compact");

        Assert.Single(Records(home.Root, "post-compact"));
    }

    [Fact]
    public void WritesNothingWhenTheHomeWasNeverInitialised()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome(initialize: false);

        var summary = """
            <engram-digest v="1">
            - This must never be written.
            </engram-digest>
            """;

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("e2e-post-compact", summary), "hook", "post-compact");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.False(File.Exists(Path.Combine(home.Root, "engram.db")));
    }

    private static string Payload(string sessionId, string compactSummary) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["session_id"] = sessionId,
            ["compact_summary"] = compactSummary,
        });

    // Keyed on path rather than scope, same reasoning as the user-prompt tests: the seed
    // corpus and other session notes could otherwise be mistaken for what this hook wrote.
    private static IReadOnlyList<string> ReadHarvestedStatements(string root)
    {
        var databasePath = Path.Combine(root, "engram.db");
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT body FROM fact WHERE path LIKE '/sessions/%/compaction-digest/%' AND valid_to IS NULL ORDER BY id;";

        var statements = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            statements.Add(reader.GetString(0));
        }

        return statements;
    }

    private static IReadOnlyList<JsonElement> Records(string root, string kind)
    {
        var path = Path.Combine(root, "telemetry.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(record => record.GetProperty("kind").GetString() == kind)
            .ToList();
    }
}
