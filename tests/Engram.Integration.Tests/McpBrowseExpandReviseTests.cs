using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The spec §9 trio: browse is a table of contents, expand is scrutiny of one handle,
/// revise is explicit belief revision through the store's own collision rule.
/// </summary>
public class McpBrowseExpandReviseTests
{
    private static readonly McpHomeState Initialized = new(true);
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Browse_ShowsCountsHereAndUnder_AndFoldsPhantomIntermediates()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/projects/browse-test", "decided", "the project exists");
            Write(connection, "/projects/browse-test", "uses", "sqlite underneath");
            Write(connection, "/projects/browse-test/code/api", "declared-as", "the api repo");
            Write(connection, "/projects/browse-test/code/api", "uses", "http");
            Write(connection, "/projects/browse-test/code/api", "imports", "nothing yet");
            Write(connection, "/projects/browse-test/decisions", "decided", "trunk releases");
        }

        var result = EngramMcpTools.Browse(
            sandbox.Home, new McpSessionId("browse-session"), Initialized, "/projects/browse-test");

        Assert.Contains("2 facts here, 4 under it", result);

        // /projects/browse-test/code was never written as an entity — only its child was —
        // yet it must appear as a folded segment carrying its subtree's count.
        Assert.Contains("code — 3 facts", result);
        Assert.Contains("decisions — 1 fact", result);
        Assert.Contains("[f", result);
        Assert.Contains("the project exists", result);
    }

    [Fact]
    public void Browse_AtDepthTwo_ReachesGrandchildren()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/projects/deep-test/code/api", "uses", "http");
        }

        var shallow = EngramMcpTools.Browse(
            sandbox.Home, new McpSessionId("s"), Initialized, "/projects/deep-test");
        var deep = EngramMcpTools.Browse(
            sandbox.Home, new McpSessionId("s"), Initialized, "/projects/deep-test", depth: 2);

        Assert.DoesNotContain("api", shallow);
        Assert.Contains("api — 1 fact", deep);
    }

    [Fact]
    public void Browse_WhereNothingIs_SaysSoInsteadOfInventingStructure()
    {
        using var sandbox = new SandboxHome();

        var result = EngramMcpTools.Browse(
            sandbox.Home, new McpSessionId("s"), Initialized, "/nowhere/at/all");

        Assert.Contains("Nothing in memory under /nowhere/at/all", result);
    }

    [Fact]
    public void Revise_ClosesTheOldBelief_AndRecordsTheReason()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Write(connection, "/projects/revise-test", "deploys-with", "jenkins on fridays");
        }

        var session = new McpSessionId("revise-session");
        var result = EngramMcpTools.Revise(
            sandbox.Home,
            session,
            Initialized,
            $"f{oldId}",
            "github actions on merge",
            "the user said jenkins was decommissioned");

        Assert.Contains("revised", result);

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var old = FactStore.ReadById(connection, oldId)!;
            Assert.NotNull(old.ValidTo);
            Assert.NotNull(old.SupersededBy);

            var replacement = FactStore.ReadById(connection, old.SupersededBy!.Value)!;
            Assert.Equal("github actions on merge", replacement.Body);
            Assert.Equal("deploys-with", replacement.Predicate);
            Assert.Null(replacement.ValidTo);

            var reasons = MemoryBrowser.Reasons(connection, [oldId]);
            Assert.Equal("the user said jenkins was decommissioned", reasons[oldId]);
        }
    }

    [Fact]
    public void Revise_OnAClosedFact_RefusesToRewriteHistory()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        long factsBefore;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Write(connection, "/projects/revise-test", "uses", "the old thing");
            FactStore.Forget(connection, oldId, "already retracted", T0);
            factsBefore = Count(connection);
        }

        var result = EngramMcpTools.Revise(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{oldId}", "the new thing", "too late");

        Assert.Contains("already closed", result);
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Assert.Equal(factsBefore, Count(connection));
        }
    }

    [Fact]
    public void Revise_WithoutAReason_DeclinesToWrite()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Write(connection, "/projects/revise-test", "uses", "something");
        }

        var result = EngramMcpTools.Revise(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{oldId}", "corrected", "  ");

        Assert.Contains("needs both", result);
    }

    [Fact]
    public void Expand_History_ShowsTheChainAndWhyItMoved()
    {
        using var sandbox = new SandboxHome();
        long oldId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            oldId = Write(connection, "/projects/expand-test", "deploys-with", "jenkins nightly");
        }

        EngramMcpTools.Revise(
            sandbox.Home,
            new McpSessionId("s"),
            Initialized,
            $"f{oldId}",
            "actions on merge",
            "pipeline was replaced");

        var history = EngramMcpTools.Expand(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{oldId}", "history");

        Assert.Contains("2 versions", history);
        Assert.Contains("jenkins nightly", history);
        Assert.Contains("actions on merge", history);
        Assert.Contains("pipeline was replaced", history);
    }

    [Fact]
    public void Expand_Evidence_TellsRegenerableFromRecorded()
    {
        using var sandbox = new SandboxHome();
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = FactStore.Remember(
                connection,
                new FactWrite(
                    "/projects/expand-test/code/api/src/Auth.cs", "file", "about",
                    "token validation lives here", "code", "observed",
                    Evidence: "src/Auth.cs @ ab12cd34", Regenerable: true),
                T0).FactId;
        }

        var result = EngramMcpTools.Expand(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{factId}", "evidence");

        Assert.Contains("src/Auth.cs @ ab12cd34", result);
        Assert.Contains("regenerable", result);
        Assert.Contains("observed", result);
    }

    [Fact]
    public void Expand_Related_ListsTheNeighbourhood()
    {
        using var sandbox = new SandboxHome();
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = Write(connection, "/projects/expand-test", "uses", "sqlite");
            Write(connection, "/projects/expand-test", "decided", "no orm");
            Write(connection, "/projects/expand-test/code/api", "uses", "http");
        }

        var result = EngramMcpTools.Expand(
            sandbox.Home, new McpSessionId("s"), Initialized, $"f{factId}", "related");

        Assert.Contains("no orm", result);
        Assert.Contains("http", result);
        Assert.DoesNotContain("sqlite", result);
    }

    [Fact]
    public void Expand_RejectsWhatItDoesNotKnow()
    {
        using var sandbox = new SandboxHome();
        long factId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            factId = Write(connection, "/projects/expand-test", "uses", "sqlite");
        }

        Assert.Contains(
            "Unknown view",
            EngramMcpTools.Expand(sandbox.Home, new McpSessionId("s"), Initialized, $"f{factId}", "vibes"));
        Assert.Contains(
            "not a fact handle",
            EngramMcpTools.Expand(sandbox.Home, new McpSessionId("s"), Initialized, "banana", "history"));
        Assert.Contains(
            "No fact with id",
            EngramMcpTools.Expand(sandbox.Home, new McpSessionId("s"), Initialized, "f999999", "history"));
    }

    private static long Write(SqliteConnection connection, string path, string predicate, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "concept", predicate, body, "project", "stated"),
            T0).FactId;

    private static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM fact;";
        return (long)command.ExecuteScalar()!;
    }
}
