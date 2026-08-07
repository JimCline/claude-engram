using Engram.Core;

namespace Engram.Integration.Tests;

public class TelemetryTests
{
    [Fact]
    public void Append_WritesOneJsonLinePerCall()
    {
        using var sandbox = new SandboxHome();

        Telemetry.Append(sandbox.Home, new TelemetryRecord("2026-08-04T00:00:00Z", "s1", TelemetryEventKind.Recall, Query: "q", FactCount: 1, TokensReturned: 10, Coverage: "high"));
        Telemetry.Append(sandbox.Home, new TelemetryRecord("2026-08-04T00:00:01Z", "s1", TelemetryEventKind.Remember));

        var path = Path.Combine(sandbox.Home.Root, "telemetry.jsonl");
        var lines = File.ReadAllLines(path);

        Assert.Equal(2, lines.Length);
        Assert.Contains("\"kind\":\"recall\"", lines[0]);
        Assert.Contains("\"kind\":\"remember\"", lines[1]);
    }

    [Fact]
    public void Append_CreatesHomeDirectoryIfMissing()
    {
        // initialize: false, so the directory this deletes never held a database — an
        // initialized home has a pooled engram.db handle, which on Windows makes this
        // very delete the thing that throws.
        using var sandbox = new SandboxHome(initialize: false);
        Directory.Delete(sandbox.Home.Root, recursive: true);

        Telemetry.Append(sandbox.Home, new TelemetryRecord("2026-08-04T00:00:00Z", "s1", TelemetryEventKind.SessionStart));

        Assert.True(File.Exists(Path.Combine(sandbox.Home.Root, "telemetry.jsonl")));
    }

    [Fact]
    public void Append_ConcurrentWriters_NeverThrows_AndAllTwentyLinesAreWellFormedJson()
    {
        using var sandbox = new SandboxHome();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 20, i =>
        {
            try
            {
                Telemetry.Append(sandbox.Home, new TelemetryRecord($"2026-08-04T00:00:{i:D2}Z", "s1", TelemetryEventKind.FileTouched));
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);

        var path = Path.Combine(sandbox.Home.Root, "telemetry.jsonl");
        var lines = File.ReadAllLines(path);
        Assert.Equal(20, lines.Length);
        foreach (var line in lines)
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
        }
    }

    [Fact]
    public void Append_HomeDirectoryCannotBeCreated_NeverThrows()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), "engram-blocking-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockingFile, "not a directory");

        try
        {
            var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var home = EngramHome.Resolve(
                Path.Combine(blockingFile, "engram-home"),
                new Dictionary<string, string?>(),
                userProfileDirectory,
                Environment.CurrentDirectory);

            var exception = Record.Exception(() =>
                Telemetry.Append(home, new TelemetryRecord("2026-08-04T00:00:00Z", "s1", TelemetryEventKind.SessionStart)));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public void Append_QueryExceedsRecordByteBudget_TruncatesQueryButStillLandsAsValidJson()
    {
        using var sandbox = new SandboxHome();
        var longQuery = new string('q', 10_000);

        Telemetry.Append(sandbox.Home, new TelemetryRecord(
            "2026-08-04T00:00:00Z", "s1", TelemetryEventKind.Recall, Query: longQuery, FactCount: 1, TokensReturned: 10, Coverage: "high"));

        var path = Path.Combine(sandbox.Home.Root, "telemetry.jsonl");
        var lines = File.ReadAllLines(path);

        Assert.Single(lines);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(lines[0]) < 4096);

        using var document = System.Text.Json.JsonDocument.Parse(lines[0]);
        var query = document.RootElement.GetProperty("query").GetString();
        Assert.NotNull(query);
        Assert.True(query!.Length < longQuery.Length);
    }
}
