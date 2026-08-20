using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

// Hazard 6: the session-open record must carry the profile the connection actually resolved at
// startup, never a fresh config read taken when the record is written.
public class ToolProfileTelemetryTests
{
    [Fact]
    public void BuildSessionOpenRecord_CarriesTheCapturedProfile_NotAFreshConfigRead()
    {
        using var sandbox = new SandboxHome(initialize: false);
        File.WriteAllText(sandbox.Home.ConfigPath, "[mcp]\ntool_profile = \"full\"\n");

        // Mirrors ServeCommand.Run: the profile is read once, before the connection is served.
        var capturedProfile = ToolProfileSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath)).Profile;
        Assert.Equal(ToolProfile.Full, capturedProfile);

        // Config changes after capture but before the session-open record is built — simulating
        // a `profile set` racing an already-open connection.
        File.WriteAllText(sandbox.Home.ConfigPath, "[mcp]\ntool_profile = \"default\"\n");

        var record = ServeCommand.BuildSessionOpenRecord("session-1", capturedProfile);

        Assert.Equal("full", record.ToolProfile);
        Assert.Equal(TelemetryEventKind.SessionOpen, record.Kind);
        Assert.Null(record.FactCount);
    }
}
