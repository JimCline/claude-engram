using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// UserPromptSubmit is the only place a fact the user states in passing can be caught,
/// so these drive the published binary rather than the JIT build — a classifier that
/// works under test and not in the shipped AOT binary would fail silently and forever.
/// </summary>
public class HookUserPromptTests
{
    // The case the feature exists for: no memory keyword anywhere in the sentence.
    [Fact]
    public void CapturesAPersonalStatementAndAsksTheModelToDateIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("I went to see a Spiderman movie last Saturday", TypedTranscript(home.Root)),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var statement = Assert.Single(ReadCapturedStatements(home.Root));
        Assert.Equal("I went to see a Spiderman movie last Saturday", statement);

        // Bare stdout is discarded on some hook events; the envelope is what actually
        // reaches the model, and its hookEventName has to match the event that produced it.
        var output = JsonNode.Parse(stdout)!;
        var hookOutput = output["hookSpecificOutput"]!;
        Assert.Equal("UserPromptSubmit", hookOutput["hookEventName"]!.GetValue<string>());

        var context = hookOutput["additionalContext"]!.GetValue<string>();
        Assert.Contains("Spiderman", context);
        Assert.Contains("supersedes", context);
    }

    [Fact]
    public void SaysAndStoresNothingForAnOrdinaryWorkingPrompt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("run the tests and tell me what fails", TypedTranscript(home.Root)),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // Sentence granularity is a privacy property, not only a precision one: the working
    // half of a mixed message must never reach disk.
    [Fact]
    public void StoresOnlyTheStatedSentenceFromAMixedMessage()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        EngramProcess.RunWithStdin(
            home.Root,
            Payload("I moved to Seattle in March. Now fix the failing test and push it.", TypedTranscript(home.Root)),
            "hook", "user-prompt");

        var statement = Assert.Single(ReadCapturedStatements(home.Root));
        Assert.Equal("I moved to Seattle in March.", statement);
        Assert.DoesNotContain("failing test", statement);
    }

    // Same rule as every other hook: an uninitialised home means do nothing, quietly.
    [Fact]
    public void WritesNothingWhenTheHomeWasNeverInitialised()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome(initialize: false);

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("I grew up in Fort Collins, Colorado", TypedTranscript(home.Root)),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.False(File.Exists(Path.Combine(home.Root, "engram.db")));
    }

    // The hook writes to the database now, and a store already holding this statement is
    // reason to say nothing rather than to write a second copy. Driving it through two real
    // processes is what makes this meaningful: the check is a query, not in-process state.
    [Fact]
    public void RepeatingAStatementCapturesItOnceAndSaysNothingTheSecondTime()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = TypedTranscript(home.Root);

        var (_, firstStdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I use a Dvorak keyboard", transcript), "hook", "user-prompt");
        var (secondExit, secondStdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I use a Dvorak keyboard.", transcript), "hook", "user-prompt");

        Assert.Contains("Dvorak", firstStdout);
        Assert.Equal(0, secondExit);
        Assert.Equal(string.Empty, secondStdout);

        Assert.Single(ReadCapturedStatements(home.Root));
    }

    // The bug this whole file exists to guard against: a cross-session peer message reads as
    // ordinary first-person prose to the classifier, exactly like a real statement would.
    // Ground-truthed against this project's own transcript, 2026-08-09/10 — every real
    // mis-capture carried promptSource "system", every real capture "typed".
    [Fact]
    public void DoesNotCaptureACrossSessionPeerMessage()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = SystemTranscript(home.Root, origin: "peer");

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("Another Claude session sent a message: I decided to use SQLite for storage.", transcript),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    [Fact]
    public void DoesNotCaptureABackgroundTaskNotification()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = SystemTranscript(home.Root, origin: "task-notification");

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root,
            Payload("<task-notification>I found the bug in the parser.</task-notification>", transcript),
            "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // Provenance is undocumented and this hook's own stdin carries none of it (D62's
    // provenance fields live only on the transcript record) — a missing or unreadable
    // transcript_path must fail closed, the same "harvest nothing, never harvest garbage"
    // rule the PostCompact harvester follows.
    [Fact]
    public void DoesNotCaptureWhenTranscriptPathIsMissingFromThePayload()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var payload = JsonSerializer.Serialize(new JsonObject
        {
            ["session_id"] = "e2e-user-prompt",
            ["prompt"] = "I prefer tabs over spaces",
        });

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(home.Root, payload, "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    [Fact]
    public void DoesNotCaptureWhenTranscriptPathPointsToAMissingFile()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var missingPath = Path.Combine(home.Root, "no-such-transcript.jsonl");

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I prefer tabs over spaces", missingPath), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    // Proves the read targets the LAST line, not the first: an earlier "typed" record must
    // not make a later "system" record — the one that actually corresponds to this
    // submission — look genuine.
    [Fact]
    public void UsesTheLastTranscriptLineNotAnEarlierOne()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var transcript = Path.Combine(home.Root, "transcript.jsonl");
        File.WriteAllText(
            transcript,
            TranscriptLine("typed", origin: null) + "\n" + TranscriptLine("system", origin: "peer") + "\n");

        var (exitCode, stdout, _) = EngramProcess.RunWithStdin(
            home.Root, Payload("I prefer tabs over spaces", transcript), "hook", "user-prompt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Empty(ReadCapturedStatements(home.Root));
    }

    private static string Payload(string prompt, string transcriptPath) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["session_id"] = "e2e-user-prompt",
            ["prompt"] = prompt,
            ["transcript_path"] = transcriptPath,
        });

    private static string TranscriptLine(string promptSource, string? origin)
    {
        var record = new JsonObject
        {
            ["type"] = "user",
            ["promptSource"] = promptSource,
        };

        if (origin is not null)
        {
            record["origin"] = new JsonObject { ["kind"] = origin };
        }

        return JsonSerializer.Serialize(record);
    }

    private static string TypedTranscript(string root)
    {
        var path = Path.Combine(root, "transcript.jsonl");
        File.WriteAllText(path, TranscriptLine("typed", origin: "human") + "\n");
        return path;
    }

    private static string SystemTranscript(string root, string origin)
    {
        var path = Path.Combine(root, "transcript.jsonl");
        File.WriteAllText(path, TranscriptLine("system", origin) + "\n");
        return path;
    }

    // Opened read-only, and through the provider rather than Engram.Core's own open routine:
    // a tier-3 test that asserts using the code under test stops saying anything about the
    // binary that ships.
    private static IReadOnlyList<string> ReadCapturedStatements(string root)
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

        // Keyed on the path, not on scope: the seed corpus is scoped 'user' too, so a scope
        // filter would report thirty-eight shipped facts as things this hook captured.
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT body FROM fact WHERE path LIKE '/user/%' AND valid_to IS NULL ORDER BY id;";

        var statements = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            statements.Add(reader.GetString(0));
        }

        return statements;
    }
}
