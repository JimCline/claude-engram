using System.Text.Json;
using Engram.Cli;

namespace Engram.Integration.Tests;

public class ActivityCommandTests
{
    private static string TelemetryPath(SandboxHome sandbox) => Path.Combine(sandbox.Home.Root, "telemetry.jsonl");

    private static void WriteFixture(SandboxHome sandbox, IEnumerable<string> lines) =>
        File.WriteAllLines(TelemetryPath(sandbox), lines);

    [Fact]
    public void Activity_RecordsInsideAndOutsideWindow_ReportsCorrectWindowCountAndLast()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        WriteFixture(sandbox, new[]
        {
            $$"""{"timestamp":"{{now.AddMinutes(-5).ToString("o")}}","session_id":"s1","kind":"session-start"}""",
            $$"""{"timestamp":"{{now.AddSeconds(-5).ToString("o")}}","session_id":"s2","kind":"recall"}""",
            $$"""{"timestamp":"{{now.AddSeconds(-2).ToString("o")}}","session_id":"s2","kind":"recall"}""",
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--since", "10s", "--json"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("WindowCount").GetInt32());
        Assert.Equal("recall", root.GetProperty("LastKind").GetString());
        Assert.Equal(10, root.GetProperty("WindowSeconds").GetInt32());
    }

    [Fact]
    public void Activity_MoreThanFiveKindsInWindow_HumanOutputCapsAtFiveAndAppendsMoreCount()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        WriteFixture(sandbox, new[]
        {
            $$"""{"timestamp":"{{now.AddSeconds(-1).ToString("o")}}","session_id":"s1","kind":"k1"}""",
            $$"""{"timestamp":"{{now.AddSeconds(-1).ToString("o")}}","session_id":"s1","kind":"k2"}""",
            $$"""{"timestamp":"{{now.AddSeconds(-1).ToString("o")}}","session_id":"s1","kind":"k3"}""",
            $$"""{"timestamp":"{{now.AddSeconds(-1).ToString("o")}}","session_id":"s1","kind":"k4"}""",
            $$"""{"timestamp":"{{now.AddSeconds(-1).ToString("o")}}","session_id":"s1","kind":"k5"}""",
            $$"""{"timestamp":"{{now.AddSeconds(-1).ToString("o")}}","session_id":"s1","kind":"k6"}""",
            $$"""{"timestamp":"{{now.AddSeconds(-1).ToString("o")}}","session_id":"s1","kind":"k7"}""",
        });

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--since", "10s"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        var text = stdout.ToString();

        // All seven tie at count 1, so ties break alphabetically (k1..k5 shown, k6/k7 folded).
        Assert.Contains("window: 7 event(s) in the last 10s — k1 1, k2 1, k3 1, k4 1, k5 1, +2 more", text);
    }

    [Fact]
    public void Activity_LastLineIsReported_EvenWhenAnEarlierLineHasALaterTimestamp()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        WriteFixture(sandbox, new[]
        {
            $$"""{"timestamp":"{{now.ToString("o")}}","session_id":"s1","kind":"recall"}""",
            $$"""{"timestamp":"{{now.AddMinutes(-10).ToString("o")}}","session_id":"s2","kind":"digest"}""",
        });

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--json"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());

        // The file is append-ordered: the last line is "digest", even though "recall" carries the
        // later timestamp. Proves the reader is not sorting.
        Assert.Equal("digest", doc.RootElement.GetProperty("LastKind").GetString());
    }

    [Fact]
    public void Activity_MalformedLineBetweenTwoGoodOnes_CountsBothAndSkipsOne()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        WriteFixture(sandbox, new[]
        {
            $$"""{"timestamp":"{{now.AddSeconds(-3).ToString("o")}}","session_id":"s1","kind":"recall"}""",
            "not json {{{",
            $$"""{"timestamp":"{{now.ToString("o")}}","session_id":"s2","kind":"digest"}""",
        });

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--json"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(2, doc.RootElement.GetProperty("WindowCount").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("SkippedLines").GetInt32());
    }

    [Fact]
    public void Activity_EveryLineMalformed_ReportsNoActivityAndTheFullSkippedCount()
    {
        using var sandbox = new SandboxHome();

        WriteFixture(sandbox, new[]
        {
            "not json {{{",
            "also not json",
            "{\"timestamp\":\"not-a-date\",\"session_id\":\"s1\",\"kind\":\"recall\"}",
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        var text = stdout.ToString();
        Assert.Contains("no activity recorded yet", text);
        Assert.Contains("3 malformed line(s) skipped.", text);
    }

    [Fact]
    public void Activity_MissingFile_ExitsZeroWithTheEmptyMessage()
    {
        using var sandbox = new SandboxHome();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("no activity recorded yet", stdout.ToString());
    }

    [Fact]
    public void ActivityJson_EmptyCase_KindsPresentAndEmpty_LastKindAbsent()
    {
        using var sandbox = new SandboxHome();

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--json"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Kinds", out var kinds));
        Assert.Equal(JsonValueKind.Array, kinds.ValueKind);
        Assert.Equal(0, kinds.GetArrayLength());
        Assert.False(root.TryGetProperty("LastKind", out _));
        Assert.Equal(0, root.GetProperty("WindowCount").GetInt32());
    }

    [Fact]
    public void Activity_WindowEmptyButHistoryHasRecords_PrintsNoActivityInWindow()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        WriteFixture(sandbox, new[]
        {
            $$"""{"timestamp":"{{now.AddMinutes(-5).ToString("o")}}","session_id":"s1","kind":"recall"}""",
        });

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--since", "10s"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        var text = stdout.ToString();
        Assert.Contains("last: recall", text);
        Assert.Contains("window: no activity in the last 10s", text);
    }

    [Fact]
    public void Activity_FutureTimestamp_ClockSkew_AgeClampsToZeroAndStillCountsInWindow()
    {
        using var sandbox = new SandboxHome();
        var now = DateTimeOffset.UtcNow;

        WriteFixture(sandbox, new[]
        {
            $$"""{"timestamp":"{{now.AddSeconds(30).ToString("o")}}","session_id":"s1","kind":"recall"}""",
        });

        var stdout = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--since", "10s"], stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("last: recall 0s ago", stdout.ToString());
        Assert.Contains("window: 1 event(s) in the last 10s", stdout.ToString());
    }

    [Fact]
    public void Activity_BadSinceValue_PrintsErrorAndExitsOne()
    {
        using var sandbox = new SandboxHome();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--since", "1.5m"], new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid --since value", stderr.ToString());
    }

    [Fact]
    public void Activity_SinceMissingValue_PrintsErrorAndExitsOne()
    {
        using var sandbox = new SandboxHome();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--since"], new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("--since requires a value", stderr.ToString());
    }

    [Fact]
    public void Activity_UnknownOption_PrintsUsageAndExitsOne()
    {
        using var sandbox = new SandboxHome();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "activity", "--bogus"], new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("usage:", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
