using Engram.Cli;
using Engram.Core;
using Microsoft.Data.Sqlite;

namespace Engram.Integration.Tests;

/// <summary>
/// The portable bundle (spec §3.2): an export is a filtered fact journal, an import is a
/// replay — additive, idempotent, and never a rewrite of what the target already believes.
/// </summary>
public class ExportImportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Export_KeepsTheSubtreeBoundary_AndStampsTheHeader()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/projects/acme/code/api", "the repo itself");
            Write(connection, "/projects/acme/code/api/src/Main.cs", "a file inside it");
            Write(connection, "/projects/acme/code/api#overview", "a fragment on the root");
            Write(connection, "/projects/acme/code/api-docs", "the sibling that shares a spelling");
        }

        var bundle = Path.Combine(sandbox.Home.Root, "bundle.jsonl");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "export", "--path", "/projects/acme/code/api", "--out", bundle],
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Exported 3 facts", stdout.ToString());

        var lines = File.ReadAllLines(bundle);
        Assert.Contains("engram-facts", lines[0]);
        Assert.Contains("/projects/acme/code/api", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.DoesNotContain(lines, line => line.Contains("api-docs"));
    }

    [Fact]
    public void RoundTrip_IsAdditive_Idempotent_AndCarriesClosure()
    {
        using var source = new SandboxHome();
        var bundle = Path.Combine(source.Home.Root, "jim.jsonl");

        // A distinctive subtree, because an initialized home is not an empty one: init
        // seeds the canned facts, and counting the whole table counts them too.
        using (var connection = EngramDatabase.OpenInitialized(source.Home))
        {
            var retired = Write(connection, "/projects/roundtrip-test", "an old belief");
            Write(connection, "/projects/roundtrip-test/notes", "a current one");
            Write(connection, "/projects/other", "not part of the bundle");
            FactStore.Forget(connection, retired, "changed his mind", T0.AddMinutes(1));
        }

        Assert.Equal(0, CliApp.Run(
            ["--home", source.Home.Root, "export", "--path", "/projects/roundtrip-test", "--out", bundle],
            new StringWriter(),
            new StringWriter()));

        using var target = new SandboxHome();
        const string InSubtree =
            "SELECT count(*) FROM fact WHERE path = '/projects/roundtrip-test' OR path LIKE '/projects/roundtrip-test/%';";

        var dryOut = new StringWriter();
        Assert.Equal(0, CliApp.Run(
            ["--home", target.Home.Root, "import", bundle],
            dryOut,
            new StringWriter()));
        Assert.Contains("Would write 2 facts", dryOut.ToString());

        using (var connection = EngramDatabase.OpenInitialized(target.Home))
        {
            Assert.Equal(0L, Count(connection, InSubtree));
        }

        var applyOut = new StringWriter();
        Assert.Equal(0, CliApp.Run(
            ["--home", target.Home.Root, "import", bundle, "--apply"],
            applyOut,
            new StringWriter()));
        Assert.Contains("Wrote 2 facts", applyOut.ToString());

        using (var connection = EngramDatabase.OpenInitialized(target.Home))
        {
            Assert.Equal(2L, Count(connection, InSubtree));
            Assert.Equal(
                1L,
                Count(connection, "SELECT count(*) FROM fact WHERE path = '/projects/roundtrip-test' AND valid_to IS NOT NULL;"));
            Assert.Equal(0L, Count(connection, "SELECT count(*) FROM fact WHERE path = '/projects/other';"));
        }

        var againOut = new StringWriter();
        Assert.Equal(0, CliApp.Run(
            ["--home", target.Home.Root, "import", bundle, "--apply"],
            againOut,
            new StringWriter()));
        Assert.Contains("Wrote 0 facts, leaving 2 already in the store", againOut.ToString());
    }

    [Fact]
    public void Export_ToStdout_KeepsTheBundleCleanOfTheSummary()
    {
        using var sandbox = new SandboxHome();
        using (var connection = EngramDatabase.OpenInitialized(sandbox.Home))
        {
            Write(connection, "/projects/stdout-test", "one fact");
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.Equal(0, CliApp.Run(
            ["--home", sandbox.Home.Root, "export", "--path", "/projects/stdout-test"],
            stdout,
            stderr));

        var lines = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.StartsWith("{", line));
        Assert.Equal(2, lines.Length);
        Assert.Contains("Exported 1 fact", stderr.ToString());
    }

    [Fact]
    public void Export_RefusesToOverwrite()
    {
        using var sandbox = new SandboxHome();
        var existing = Path.Combine(sandbox.Home.Root, "precious.jsonl");
        File.WriteAllText(existing, "somebody's only copy");

        var stderr = new StringWriter();
        var exitCode = CliApp.Run(
            ["--home", sandbox.Home.Root, "export", "--out", existing],
            new StringWriter(),
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("already exists", stderr.ToString());
        Assert.Equal("somebody's only copy", File.ReadAllText(existing));
    }

    [Fact]
    public void Import_WithoutAFile_SaysHowToCallIt()
    {
        using var sandbox = new SandboxHome();

        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "import"], new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("engram import <file>", stderr.ToString());
    }

    [Fact]
    public void Export_WithoutAStore_NamesInit()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "export"], new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("engram init", stderr.ToString());
        Assert.False(File.Exists(sandbox.Home.DatabasePath));
    }

    private static long Write(SqliteConnection connection, string path, string body) =>
        FactStore.Remember(
            connection,
            new FactWrite(path, "concept", "states", body, "project", "stated"),
            T0).FactId;

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
