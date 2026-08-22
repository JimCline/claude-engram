using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

public class DirectiveFactsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AddThenReadLive_RoundTrips()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0);

        var live = DirectiveFacts.ReadLive(connection);

        var directive = Assert.Single(live);
        Assert.Equal("always use BEGIN IMMEDIATE for writes", directive.Body);
        Assert.Null(directive.ValidTo);
    }

    [Fact]
    public void ReadLive_OrdersOldestFirst()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        DirectiveFacts.Add(connection, "first directive", T0);
        DirectiveFacts.Add(connection, "second directive", T0.AddSeconds(1));

        var live = DirectiveFacts.ReadLive(connection);

        Assert.Equal(["first directive", "second directive"], live.Select(d => d.Body));
    }

    [Fact]
    public void ARequiresFactAtAnUnrelatedPath_NeverAppearsAmongDirectives()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        FactStore.Remember(
            connection,
            new FactWrite("/facts/tabs", "note", "requires", "always use spaces", "user", "stated"),
            T0);
        DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0);

        var live = DirectiveFacts.ReadLive(connection);

        var directive = Assert.Single(live);
        Assert.Equal("always use BEGIN IMMEDIATE for writes", directive.Body);
    }

    // ix_fact_path exists specifically so this range scan can seek rather than full-scan
    // fact — LIKE/substr would silently degrade to a scan and defeat the whole point of the
    // denormalized path column on a hook path (D60).
    [Fact]
    public void ReadLive_UsesTheIndexRatherThanScanningFact()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);

        for (var i = 0; i < 50; i++)
        {
            FactStore.Remember(
                connection,
                new FactWrite($"/facts/note-{i}", "note", "states", $"note {i}", "user", "stated"),
                T0);
        }

        DirectiveFacts.Add(connection, "always use BEGIN IMMEDIATE for writes", T0);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT id, body, valid_from, valid_to, superseded_by
              FROM fact
             WHERE path >= '/directives/' AND path < '/directives0'
               AND valid_to IS NULL
             ORDER BY valid_from;
            """;

        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }

        Assert.Contains(lines, l => l.Contains("SEARCH", StringComparison.Ordinal) && l.Contains("fact", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("SCAN", StringComparison.Ordinal) && l.Contains("fact", StringComparison.Ordinal));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDirective(EngramHome home, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = DirectiveCommand.Run(home.Root, args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void CliAdd_ThenList_ShowsIt()
    {
        using var sandbox = new SandboxHome(initialize: true);

        var (addExit, addOut, _) = RunDirective(sandbox.Home, "add", "always use BEGIN IMMEDIATE for writes");
        Assert.Equal(0, addExit);
        Assert.Contains("added", addOut, StringComparison.Ordinal);

        var (listExit, listOut, _) = RunDirective(sandbox.Home, "list");
        Assert.Equal(0, listExit);
        Assert.Contains("always use BEGIN IMMEDIATE for writes", listOut);
    }

    [Fact]
    public void CliRemove_WithoutApply_ChangesNothing()
    {
        using var sandbox = new SandboxHome(initialize: true);
        RunDirective(sandbox.Home, "add", "always use BEGIN IMMEDIATE for writes");

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var id = Assert.Single(DirectiveFacts.ReadLive(connection)).Id;

            var (exit, dryRunOut, _) = RunDirective(sandbox.Home, "remove", FactCatalog.HandleFor(id));
            Assert.Equal(0, exit);
            Assert.Contains("Would retire", dryRunOut, StringComparison.Ordinal);
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Assert.Single(DirectiveFacts.ReadLive(connection));
        }
    }

    [Fact]
    public void CliRemove_WithApply_Retires()
    {
        using var sandbox = new SandboxHome(initialize: true);
        RunDirective(sandbox.Home, "add", "always use BEGIN IMMEDIATE for writes");

        long id;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            id = Assert.Single(DirectiveFacts.ReadLive(connection)).Id;
        }

        var (exit, _, _) = RunDirective(sandbox.Home, "remove", FactCatalog.HandleFor(id), "--apply");
        Assert.Equal(0, exit);

        using var after = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Empty(DirectiveFacts.ReadLive(after));
        Assert.Single(DirectiveFacts.ReadAll(after));
    }

    [Fact]
    public void CliRevise_WithoutApply_ChangesNothing()
    {
        using var sandbox = new SandboxHome(initialize: true);
        RunDirective(sandbox.Home, "add", "always use BEGIN IMMEDIATE for writes");

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var id = Assert.Single(DirectiveFacts.ReadLive(connection)).Id;

            var (exit, dryRunOut, _) = RunDirective(
                sandbox.Home, "revise", FactCatalog.HandleFor(id), "always use BEGIN IMMEDIATE, no exceptions");
            Assert.Equal(0, exit);
            Assert.Contains("Would replace", dryRunOut, StringComparison.Ordinal);
        }

        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var live = Assert.Single(DirectiveFacts.ReadLive(connection));
            Assert.Equal("always use BEGIN IMMEDIATE for writes", live.Body);
        }
    }

    [Fact]
    public void CliRevise_WithApply_ClosesTheOldOneAndOpensANewOneOnTheSameThread()
    {
        using var sandbox = new SandboxHome(initialize: true);
        RunDirective(sandbox.Home, "add", "always use BEGIN IMMEDIATE for writes");

        long id;
        string oldPath;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var directive = Assert.Single(DirectiveFacts.ReadLive(connection));
            id = directive.Id;
            oldPath = FactStore.ReadById(connection, id)!.SubjectPath;
        }

        var (exit, _, _) = RunDirective(
            sandbox.Home, "revise", FactCatalog.HandleFor(id), "always use BEGIN IMMEDIATE, no exceptions", "--apply");
        Assert.Equal(0, exit);

        using var after = EngramDatabase.OpenInitialized(sandbox.Home);
        var live = Assert.Single(DirectiveFacts.ReadLive(after));
        Assert.Equal("always use BEGIN IMMEDIATE, no exceptions", live.Body);

        var closed = FactStore.ReadById(after, id)!;
        Assert.NotNull(closed.ValidTo);
        Assert.NotNull(closed.SupersededBy);
        Assert.Equal("always use BEGIN IMMEDIATE for writes", closed.Body);

        var newFact = FactStore.ReadById(after, live.Id)!;
        Assert.Equal(oldPath, newFact.SubjectPath);
    }

    [Fact]
    public void CliAdd_PastTheCap_IsRefusedAndLeavesTheStoreUnchanged()
    {
        using var sandbox = new SandboxHome(initialize: true);

        // Trimmed down from an oversized string until its estimate lands exactly on the cap —
        // at that point any further nonzero addition must overrun it, since TokenEstimator
        // rounds up and a non-empty statement always costs at least one token.
        var big = new string('w', DirectiveFacts.MaxDirectiveTokens * 4);
        while (TokenEstimator.Estimate(big) > DirectiveFacts.MaxDirectiveTokens)
        {
            big = big[..^1];
        }

        Assert.Equal(DirectiveFacts.MaxDirectiveTokens, TokenEstimator.Estimate(big));

        var (firstExit, _, _) = RunDirective(sandbox.Home, "add", big);
        Assert.Equal(0, firstExit);

        var (secondExit, _, secondErr) = RunDirective(sandbox.Home, "add", "one more word");
        Assert.Equal(1, secondExit);
        Assert.Contains("Refused", secondErr, StringComparison.OrdinalIgnoreCase);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Single(DirectiveFacts.ReadLive(connection));
    }

    // The boundary itself: a directive whose estimate lands exactly at the cap is accepted —
    // the refusal is ">", not ">=".
    [Fact]
    public void CliAdd_ExactlyAtTheCap_IsAccepted()
    {
        using var sandbox = new SandboxHome(initialize: true);

        var exact = new string('w', DirectiveFacts.MaxDirectiveTokens * 4);
        while (TokenEstimator.Estimate(exact) > DirectiveFacts.MaxDirectiveTokens)
        {
            exact = exact[..^1];
        }

        var (exit, _, _) = RunDirective(sandbox.Home, "add", exact);
        Assert.Equal(0, exit);

        using var connection = EngramDatabase.OpenInitialized(sandbox.Home);
        Assert.Single(DirectiveFacts.ReadLive(connection));
    }

    [Fact]
    public void RemoveOrRevise_RefusesAFactThatIsNotADirective()
    {
        using var sandbox = new SandboxHome(initialize: true);

        long requiresFactId;
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            var result = FactStore.Remember(
                connection,
                new FactWrite("/facts/tabs", "note", "requires", "always use spaces", "user", "stated"),
                T0);
            requiresFactId = result.FactId;
        }

        var handle = FactCatalog.HandleFor(requiresFactId);

        var (removeExit, _, removeErr) = RunDirective(sandbox.Home, "remove", handle, "--apply");
        Assert.Equal(1, removeExit);
        Assert.Contains("no live directive", removeErr, StringComparison.OrdinalIgnoreCase);

        var (reviseExit, _, reviseErr) = RunDirective(sandbox.Home, "revise", handle, "always use tabs", "--apply");
        Assert.Equal(1, reviseExit);
        Assert.Contains("no live directive", reviseErr, StringComparison.OrdinalIgnoreCase);

        var (listExit, listOut, _) = RunDirective(sandbox.Home, "list", "--all");
        Assert.Equal(0, listExit);
        Assert.DoesNotContain("always use spaces", listOut, StringComparison.Ordinal);

        using var after = EngramDatabase.OpenInitialized(sandbox.Home);
        var requiresFact = FactStore.ReadById(after, requiresFactId)!;
        Assert.Null(requiresFact.ValidTo);
        Assert.Equal("always use spaces", requiresFact.Body);
    }
}
