using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// docs/memory-expansion/03-tool-profiles-spec.md: doctor reports the active [mcp] tool_profile,
/// reading config directly and never opening a connection (D37).
/// </summary>
public class ToolProfileDoctorTests
{
    private static Diagnosis ToolProfileCheck(SandboxHome sandbox) => Assert.Single(
        Diagnostics.Run(sandbox.Home, _ => null, reachOut: false).Checks,
        check => check.Name == "tool profile");

    [Fact]
    public void DefaultProfile_ReportsOk()
    {
        using var sandbox = new SandboxHome();

        var check = ToolProfileCheck(sandbox);

        Assert.Equal(DiagnosisState.Ok, check.State);
        Assert.Contains("default", check.Detail);
    }

    // Falsify: change the DiagnosisState.Ok in CheckToolProfile's full branch to Warn and confirm
    // this fails — "full" is as deliberate and fully-supported a choice as "default" (D37: a
    // diagnostic that reports a choice as a fault is one people stop reading), so it reports Ok
    // too, differing from "default" only in message text.
    [Fact]
    public void FullProfile_ReportsOk()
    {
        using var sandbox = new SandboxHome(initialize: false);
        File.WriteAllText(sandbox.Home.ConfigPath, "[mcp]\ntool_profile = \"full\"\n");

        var check = ToolProfileCheck(sandbox);

        Assert.Equal(DiagnosisState.Ok, check.State);
        Assert.Contains("full", check.Detail);
    }

    [Fact]
    public void MalformedValue_FallsBackToDefault_AndWarnsNamingTheBadValue()
    {
        using var sandbox = new SandboxHome(initialize: false);
        File.WriteAllText(sandbox.Home.ConfigPath, "[mcp]\ntool_profile = \"everything\"\n");

        var check = ToolProfileCheck(sandbox);

        Assert.Equal(DiagnosisState.Warn, check.State);
        Assert.Contains("everything", check.Detail);
    }
}
