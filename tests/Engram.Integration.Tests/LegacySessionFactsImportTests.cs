using System.Text.Json.Nodes;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The migration off the per-session JSONL files. Notes from earlier sessions are what
/// recall's prior-session tier is made of, so an upgrade that dropped them would quietly
/// empty a tier the model is told to expect.
/// </summary>
public class LegacySessionFactsImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<StoredFact> LiveNotes(EngramHome home)
    {
        using var connection = EngramDatabase.OpenInitialized(home);
        return FactStore.ReadSubtree(connection, SessionFacts.Root);
    }

    [Fact]
    public void ExistingNotesSurviveTheMoveAndAreReadBackForTheirOwnSession()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "sess-a", "s001", "2026-08-01T10:00:00.0000000Z", "Retries are capped at three.");
        WriteLegacy(sandbox.Home, "sess-b", "s001", "2026-08-01T11:00:00.0000000Z", "The build fails on net9.0.");

        EngramInitializer.Initialize(sandbox.Home);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var (current, prior) = SessionFacts.Read(connection, "sess-a", Now);

        Assert.Equal("Retries are capped at three.", Assert.Single(current).Statement);
        Assert.Equal("The build fails on net9.0.", Assert.Single(prior).Statement);
    }

    // The file is what actually grouped these notes. A record whose own session_id disagrees
    // with the file it sits in would otherwise reparent that note into a session it was never
    // taken in — and the prior-session discriminator would then group it with strangers.
    [Fact]
    public void TheSessionComesFromTheFileNameNotTheRecordsOwnField()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(
            sandbox.Home, "sess-a", "s001", "2026-08-01T10:00:00.0000000Z", "Retries are capped at three.",
            recordSessionId: "some-other-session");

        EngramInitializer.Initialize(sandbox.Home);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        Assert.NotNull(SessionStore.FindSession(connection, "sess-a"));
        Assert.Null(SessionStore.FindSession(connection, "some-other-session"));
    }

    [Fact]
    public void SubagentAttributionSurvivesTheMove()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(
            sandbox.Home, "sess-a", "s001", "2026-08-01T10:00:00.0000000Z", "Found the caller.",
            agent: "task-gopher:task-gopher");

        EngramInitializer.Initialize(sandbox.Home);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var (current, _) = SessionFacts.Read(connection, "sess-a", Now);

        Assert.Equal("task-gopher:task-gopher", Assert.Single(current).Agent);
    }

    // The whole point of moving is that a note can now be retracted. An import that ran again
    // on the next upgrade would hand back every note the user has since dropped.
    [Fact]
    public void ImportingTwiceDoesNothingTheSecondTime()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "sess-a", "s001", "2026-08-01T10:00:00.0000000Z", "Retries are capped at three.");

        EngramInitializer.Initialize(sandbox.Home);

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var note = Assert.Single(FactStore.ReadSubtree(connection, SessionFacts.Root));
            FactStore.Forget(connection, note.Id, "retracted by the user", Now);
        }

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Empty(LiveNotes(sandbox.Home));
    }

    // The JSONL is the only copy of the pre-migration state; an upgrade does not get to
    // destroy it just because it no longer reads it on the hot path.
    [Fact]
    public void TheJsonlFilesAreLeftOnDisk()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "sess-a", "s001", "2026-08-01T10:00:00.0000000Z", "Retries are capped at three.");

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Single(Directory.GetFiles(Path.Combine(sandbox.Home.Root, "sessions"), "*.jsonl"));
    }

    [Fact]
    public void AHalfWrittenLineDoesNotTakeTheRestOfTheImportDownWithIt()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "sess-a", "s001", "2026-08-01T10:00:00.0000000Z", "A well-formed note.");
        File.AppendAllText(Path.Combine(sandbox.Home.Root, "sessions", "sess-a.jsonl"), "{\"statement\": \"trunc\n");

        EngramInitializer.Initialize(sandbox.Home);

        var note = Assert.Single(LiveNotes(sandbox.Home));
        Assert.Equal("A well-formed note.", note.Body);
    }

    [Fact]
    public void AnAbsentDirectoryIsNotAnError()
    {
        using var sandbox = new SandboxHome(initialize: false);

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Empty(LiveNotes(sandbox.Home));
    }

    private static void WriteLegacy(
        EngramHome home,
        string sessionFile,
        string id,
        string timestamp,
        string statement,
        string? agent = null,
        string? recordSessionId = null)
    {
        var sessionsDir = Path.Combine(home.Root, "sessions");
        Directory.CreateDirectory(sessionsDir);

        var record = new JsonObject
        {
            ["id"] = id,
            ["timestamp"] = timestamp,
            ["session_id"] = recordSessionId ?? sessionFile,
            ["statement"] = statement,
        };

        if (agent is not null)
        {
            record["agent"] = agent;
        }

        File.AppendAllText(Path.Combine(sessionsDir, sessionFile + ".jsonl"), record.ToJsonString() + "\n");
    }
}
