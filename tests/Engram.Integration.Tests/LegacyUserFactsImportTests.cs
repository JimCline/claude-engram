using System.Text.Json.Nodes;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The migration off the JSON directory. A storage change that silently drops what a user
/// told the system is data loss, so these are about the captures that already exist on a
/// real instance rather than about the new write path.
/// </summary>
public class LegacyUserFactsImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Live captures only. Keyed on the subtree rather than on scope, because the seed corpus
    /// is scoped <c>user</c> too and a scope filter would match all fifty-one seeded facts.
    /// </summary>
    private static IReadOnlyList<StoredFact> LiveCaptures(EngramHome home)
    {
        using var connection = EngramDatabase.OpenInitialized(home);
        return FactStore.ReadSubtree(connection, UserFacts.Root);
    }

    [Fact]
    public void ExistingCapturesSurviveTheMoveAndAreRecallable()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "u00000001", "2026-08-01T10:00:00.0000000Z", "personal", "I grew up in Fort Collins");
        WriteLegacy(sandbox.Home, "u00000002", "2026-08-01T10:00:01.0000000Z", "directive", "always use BEGIN IMMEDIATE");

        EngramInitializer.Initialize(sandbox.Home);

        var facts = LiveCaptures(sandbox.Home);

        Assert.Equal(2, facts.Count);
        Assert.Contains(facts, f => f.Predicate == "stated" && f.Body == "I grew up in Fort Collins");
        Assert.Contains(facts, f => f.Predicate == "requires" && f.Body == "always use BEGIN IMMEDIATE");

        // Imported is not the same as reachable: recall reads through its own path, and a
        // fact that lands where that path cannot see it has not really been migrated.
        var recalled = FactCatalog.ReadLongTerm(sandbox.Home, Now);
        Assert.Contains(recalled, f => f.Body == "I grew up in Fort Collins" && f.Topic == "about you");
        Assert.Contains(recalled, f => f.Body == "always use BEGIN IMMEDIATE" && f.Topic == "your standing instructions");
    }

    // Replayed, not collapsed. The whole reason for moving is that the store records why a
    // belief changed, and an import that wrote only the surviving text would arrive with
    // that history already thrown away.
    [Fact]
    public void ARewriteImportsAsASupersessionOfTheCaptureItReplaced()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "u00000001", "2026-08-01T10:00:00.0000000Z", "personal", "I saw a movie last Saturday");
        WriteLegacy(
            sandbox.Home, "u00000002", "2026-08-01T10:05:00.0000000Z", "personal",
            "Saw a Spider-Man film on 2026-08-01", supersedes: "u00000001");

        EngramInitializer.Initialize(sandbox.Home);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var history = FactStore.History(
            connection,
            UserFacts.PathFor(UserFactTopic.AboutYou, "I saw a movie last Saturday"),
            UserFacts.PredicateFor(UserFactTopic.AboutYou));

        Assert.Equal(2, history.Count);
        Assert.Equal("I saw a movie last Saturday", history[0].Body);
        Assert.NotNull(history[0].ValidTo);
        Assert.Equal(history[1].Id, history[0].SupersededBy);
        Assert.Equal("Saw a Spider-Man film on 2026-08-01", history[1].Body);
        Assert.Null(history[1].ValidTo);
    }

    // Handing a retracted statement back during an upgrade is the worst version of getting
    // this wrong: the user asked for it to be gone and the migration reinstates it.
    [Fact]
    public void ARetractedCaptureImportsClosed()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "u00000001", "2026-08-01T10:00:00.0000000Z", "personal", "I saw a film last Saturday");
        WriteLegacy(
            sandbox.Home, "u00000002", "2026-08-01T10:05:00.0000000Z", "retraction",
            "retracted u00000001", retracts: "u00000001");

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Empty(LiveCaptures(sandbox.Home));

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        var history = FactStore.History(
            connection,
            UserFacts.PathFor(UserFactTopic.AboutYou, "I saw a film last Saturday"),
            UserFacts.PredicateFor(UserFactTopic.AboutYou));

        var fact = Assert.Single(history);
        Assert.NotNull(fact.ValidTo);
        Assert.Null(fact.SupersededBy);
    }

    // Retracting a fact that had already been rewritten has to close the rewrite, not the
    // raw capture the id happens to name. They share an address for exactly this reason.
    [Fact]
    public void RetractingARewrittenCaptureClosesTheVersionThatWasLive()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "u00000001", "2026-08-01T10:00:00.0000000Z", "personal", "I saw a movie last Saturday");
        WriteLegacy(
            sandbox.Home, "u00000002", "2026-08-01T10:05:00.0000000Z", "personal",
            "Saw a Spider-Man film on 2026-08-01", supersedes: "u00000001");
        WriteLegacy(
            sandbox.Home, "u00000003", "2026-08-01T10:10:00.0000000Z", "retraction",
            "retracted u00000002", retracts: "u00000002");

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Empty(LiveCaptures(sandbox.Home));
    }

    // init runs on every upgrade, so an import that is not once-only would re-add facts the
    // user has since forgotten, every time.
    [Fact]
    public void ImportingTwiceDoesNothingTheSecondTime()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "u00000001", "2026-08-01T10:00:00.0000000Z", "personal", "I grew up in Fort Collins");

        EngramInitializer.Initialize(sandbox.Home);

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var factId = FactStore.FindLiveFactId(
                connection,
                transaction: null,
                UserFacts.PathFor(UserFactTopic.AboutYou, "I grew up in Fort Collins"),
                UserFacts.PredicateFor(UserFactTopic.AboutYou))!.Value;

            FactStore.Forget(connection, factId, "retracted by the user", Now);
        }

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Empty(LiveCaptures(sandbox.Home));
    }

    // The JSON is the only copy of the pre-migration state; an upgrade does not get to
    // destroy it just because it no longer reads it on the hot path.
    [Fact]
    public void TheJsonFilesAreLeftOnDisk()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "u00000001", "2026-08-01T10:00:00.0000000Z", "personal", "I grew up in Fort Collins");

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Single(Directory.GetFiles(sandbox.Home.UserFactsDir, "*.json"));
    }

    [Fact]
    public void AHalfWrittenFileDoesNotTakeTheRestOfTheImportDownWithIt()
    {
        using var sandbox = new SandboxHome(initialize: false);

        WriteLegacy(sandbox.Home, "u00000001", "2026-08-01T10:00:00.0000000Z", "personal", "I use a Dvorak keyboard");
        File.WriteAllText(Path.Combine(sandbox.Home.UserFactsDir, "999-0-utruncated.json"), "{\"id\": \"utrunc");

        EngramInitializer.Initialize(sandbox.Home);

        var fact = Assert.Single(LiveCaptures(sandbox.Home));
        Assert.Equal("I use a Dvorak keyboard", fact.Body);
    }

    [Fact]
    public void AnAbsentDirectoryIsNotAnError()
    {
        using var sandbox = new SandboxHome(initialize: false);

        EngramInitializer.Initialize(sandbox.Home);

        Assert.Empty(LiveCaptures(sandbox.Home));
    }

    private static void WriteLegacy(
        EngramHome home,
        string id,
        string timestamp,
        string kind,
        string statement,
        string? supersedes = null,
        string? retracts = null)
    {
        Directory.CreateDirectory(home.UserFactsDir);

        var record = new JsonObject
        {
            ["id"] = id,
            ["timestamp"] = timestamp,
            ["kind"] = kind,
            ["statement"] = statement,
            ["session_id"] = "legacy-session",
        };

        if (supersedes is not null)
        {
            record["supersedes"] = supersedes;
        }

        if (retracts is not null)
        {
            record["retracts"] = retracts;
        }

        File.WriteAllText(Path.Combine(home.UserFactsDir, $"{timestamp.Replace(':', '-')}-{id}.json"), record.ToJsonString());
    }
}
