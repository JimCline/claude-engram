using System.Text.Json;
using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// <c>status --json</c>. <see cref="StatusCommand.ExitCodeFor"/> and
/// <see cref="StatusCommand.BuildJson"/> are exercised directly against hand-built
/// <see cref="StatusResult"/>s — reaching every <see cref="ServerStatusKind"/> through a real
/// <see cref="ServerLifecycle"/> would need a live process per kind, which none of the existing
/// CLI commands wire up for testing (see the implementation report for this gap). The one live
/// <c>CliApp.Run</c> case below (<see cref="ServerStatusKind.NotRunning"/>) is reachable without
/// any process at all — no pid file means <see cref="ServerLifecycle.Status"/> never touches the
/// network — so it proves the JSON and human renderers actually agree on the exit code they share.
/// </summary>
public class StatusCommandJsonTests
{
    private const string Version = "0.1.0";
    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ServerStatusKind.Running, 0)]
    [InlineData(ServerStatusKind.NotRunning, 1)]
    [InlineData(ServerStatusKind.Stale, 1)]
    [InlineData(ServerStatusKind.Wedged, 1)]
    [InlineData(ServerStatusKind.Reused, 1)]
    [InlineData(ServerStatusKind.VersionMismatch, 1)]
    public void ExitCodeFor_MatchesTheDocumentedRule(ServerStatusKind kind, int expected)
    {
        Assert.Equal(expected, StatusCommand.ExitCodeFor(kind));
    }

    [Fact]
    public void BuildJson_Running_IncludesHealthFieldsAndUptime()
    {
        var health = new HealthResponsePayload(123, 7433, Version, DateTimeOffset.UtcNow.AddMinutes(-5));
        var status = new StatusResult(ServerStatusKind.Running, null, health);

        var json = StatusCommand.BuildJson(FakeHome(), initialized: true, "/opt/engram/engram", status);

        Assert.Equal("Running", json.Server);
        Assert.Equal(123, json.Pid);
        Assert.Equal(7433, json.Port);
        Assert.Equal(Version, json.Version);
        Assert.NotNull(json.UptimeSeconds);
        Assert.True(json.UptimeSeconds >= 0);
    }

    [Fact]
    public void BuildJson_NotRunning_OmitsRunningOnlyFieldsFromTheSerializedDocument()
    {
        var status = new StatusResult(ServerStatusKind.NotRunning, null, null);

        var json = StatusCommand.BuildJson(FakeHome(), initialized: false, "/opt/engram/engram", status);

        Assert.Equal("NotRunning", json.Server);
        Assert.Null(json.Pid);
        Assert.Null(json.Port);
        Assert.Null(json.Version);
        Assert.Null(json.UptimeSeconds);

        var serialized = JsonSerializer.Serialize(json, StatusJsonContext.Default.StatusJson);
        using var doc = JsonDocument.Parse(serialized);

        // The half WhenWritingNull buys: absent keys, not explicit nulls.
        Assert.False(doc.RootElement.TryGetProperty("Pid", out _));
        Assert.False(doc.RootElement.TryGetProperty("Port", out _));
        Assert.False(doc.RootElement.TryGetProperty("UptimeSeconds", out _));
    }

    [Fact]
    public void BuildJson_Wedged_TakesPidFromRecordedRatherThanHealth()
    {
        var recorded = new PidFileRecord(456, 7433, Version, StartTime);
        var status = new StatusResult(ServerStatusKind.Wedged, recorded, null);

        var json = StatusCommand.BuildJson(FakeHome(), initialized: true, "/opt/engram/engram", status);

        Assert.Equal(456, json.Pid);
    }

    [Fact]
    public void BuildJson_VersionMismatch_ReportsTheRunningServersVersion()
    {
        var health = new HealthResponsePayload(789, 7433, "0.0.9", StartTime);
        var status = new StatusResult(ServerStatusKind.VersionMismatch, null, health);

        var json = StatusCommand.BuildJson(FakeHome(), initialized: true, "/opt/engram/engram", status);

        Assert.Equal("0.0.9", json.Version);
        Assert.Equal(789, json.Pid);
    }

    [Fact]
    public void BuildJson_StartedFromDiffersFromThisBinary_EmitsBoth()
    {
        var health = new HealthResponsePayload(1, 7433, Version, StartTime);
        var status = new StatusResult(ServerStatusKind.Running, null, health, "/opt/other/engram");

        var json = StatusCommand.BuildJson(FakeHome(), initialized: true, "/opt/engram/engram", status);

        Assert.Equal("/opt/other/engram", json.StartedFrom);
        Assert.Equal("/opt/engram/engram", json.ThisBinary);
    }

    [Fact]
    public void BuildJson_StartedFromMatchesThisBinary_OmitsThisBinary()
    {
        var health = new HealthResponsePayload(1, 7433, Version, StartTime);
        var status = new StatusResult(ServerStatusKind.Running, null, health, "/opt/engram/engram");

        var json = StatusCommand.BuildJson(FakeHome(), initialized: true, "/opt/engram/engram", status);

        Assert.Equal("/opt/engram/engram", json.StartedFrom);
        Assert.Null(json.ThisBinary);
    }

    [Fact]
    public void Run_JsonAndHuman_ReturnTheSameExitCode_ForNotRunning()
    {
        using var sandbox = new SandboxHome();

        var jsonOut = new StringWriter();
        var jsonExit = CliApp.Run(["--home", sandbox.Home.Root, "status", "--json"], jsonOut, new StringWriter());

        var textOut = new StringWriter();
        var textExit = CliApp.Run(["--home", sandbox.Home.Root, "status"], textOut, new StringWriter());

        Assert.Equal(textExit, jsonExit);
        Assert.Equal(1, jsonExit);

        using var doc = JsonDocument.Parse(jsonOut.ToString());
        Assert.Equal("NotRunning", doc.RootElement.GetProperty("Server").GetString());
    }

    [Fact]
    public void Run_UnknownFlag_PrintsUsageAndExitsOne()
    {
        using var sandbox = new SandboxHome();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["--home", sandbox.Home.Root, "status", "--bogus"], new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("usage:", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static EngramHome FakeHome() =>
        EngramHome.Resolve("/tmp/engram-status-json-test-home", new Dictionary<string, string?>(), "/tmp/fake-profile", "/tmp");
}
