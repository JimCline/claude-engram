using System.Text.Json.Nodes;
using Engram.Core;

namespace Engram.Integration.Tests;

public class ClaudePermissionsTests
{
    private static string SettingsIn(SandboxHome sandbox) =>
        Path.Combine(sandbox.Home.Root, "claude-settings.json");

    private static HashSet<string> AllowListOf(string settingsPath)
    {
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settingsPath))!;
        var allow = (JsonArray)root["permissions"]!["allow"]!;
        return [.. allow.Select(entry => entry!.GetValue<string>())];
    }

    // The exclusion is the whole point of enumerating instead of writing one wildcard, and a
    // wildcard is exactly what a later "simplification" would reach for. If engram_forget ever
    // appears in this list, memory can be closed without anyone being asked.
    [Fact]
    public void NeverGrantsForgetOrTheLifecycleTools()
    {
        Assert.DoesNotContain("mcp__plugin_engram_engram__engram_forget", ClaudePermissions.GrantedTools);
        Assert.DoesNotContain("mcp__plugin_engram_engram__engram_start", ClaudePermissions.GrantedTools);
        Assert.DoesNotContain("mcp__plugin_engram_engram__engram_stop", ClaudePermissions.GrantedTools);
        Assert.DoesNotContain(ClaudePermissions.GrantedTools, tool => tool.Contains('*', StringComparison.Ordinal));
    }

    [Fact]
    public void CreatesTheSettingsFileWhenThereIsNoneYet()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);

        var plan = ClaudePermissions.PlanGrant(settings);
        Assert.False(plan.SettingsFileExisted);

        ClaudePermissions.ApplyGrant(plan, sandbox.Home.GrantedPermissionsPath);

        Assert.Equal(ClaudePermissions.GrantedTools.ToHashSet(), AllowListOf(settings));
    }

    [Fact]
    public void LeavesEverythingElseInTheSettingsFileAlone()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);
        File.WriteAllText(
            settings,
            """
            {
              "model": "opus",
              "permissions": { "allow": ["Bash(git status)"], "deny": ["Bash(rm -rf /)"] },
              "env": { "FOO": "bar" }
            }
            """);

        ClaudePermissions.ApplyGrant(ClaudePermissions.PlanGrant(settings), sandbox.Home.GrantedPermissionsPath);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal("bar", root["env"]!["FOO"]!.GetValue<string>());
        Assert.Equal("Bash(rm -rf /)", ((JsonArray)root["permissions"]!["deny"]!)[0]!.GetValue<string>());
        Assert.Contains("Bash(git status)", AllowListOf(settings));
        Assert.Contains("mcp__plugin_engram_engram__engram_recall", AllowListOf(settings));
    }

    [Fact]
    public void GrantingTwiceAddsEachToolOnce()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);

        ClaudePermissions.ApplyGrant(ClaudePermissions.PlanGrant(settings), sandbox.Home.GrantedPermissionsPath);
        var second = ClaudePermissions.PlanGrant(settings);

        Assert.Empty(second.ToAdd);
        Assert.Equal(ClaudePermissions.GrantedTools.Count, second.AlreadyPresent.Count);

        ClaudePermissions.ApplyGrant(second, sandbox.Home.GrantedPermissionsPath);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal(ClaudePermissions.GrantedTools.Count, ((JsonArray)root["permissions"]!["allow"]!).Count);
    }

    // The case the record file exists for. An entry the user put there themselves must survive
    // an uninstall, because taking it back would be editing a decision that was never ours.
    [Fact]
    public void RemovalTakesBackOnlyWhatItAdded()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);
        File.WriteAllText(
            settings,
            """
            { "permissions": { "allow": ["mcp__plugin_engram_engram__engram_recall"] } }
            """);

        var grant = ClaudePermissions.PlanGrant(settings);
        Assert.Contains("mcp__plugin_engram_engram__engram_recall", grant.AlreadyPresent);
        ClaudePermissions.ApplyGrant(grant, sandbox.Home.GrantedPermissionsPath);

        var revoke = ClaudePermissions.PlanRevoke(settings, sandbox.Home.GrantedPermissionsPath);
        Assert.Contains("mcp__plugin_engram_engram__engram_recall", revoke.LeftAlone);
        Assert.DoesNotContain("mcp__plugin_engram_engram__engram_recall", revoke.ToRemove);

        ClaudePermissions.ApplyRevoke(revoke, sandbox.Home.GrantedPermissionsPath);

        var remaining = AllowListOf(settings);
        Assert.Equal(["mcp__plugin_engram_engram__engram_recall"], remaining);
    }

    [Fact]
    public void RemovalWithNoRecordRemovesNothing()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);
        File.WriteAllText(
            settings,
            """
            { "permissions": { "allow": ["mcp__plugin_engram_engram__engram_digest"] } }
            """);

        var revoke = ClaudePermissions.PlanRevoke(settings, sandbox.Home.GrantedPermissionsPath);

        Assert.Empty(revoke.ToRemove);
        Assert.Contains("mcp__plugin_engram_engram__engram_digest", revoke.LeftAlone);
    }

    [Fact]
    public void RoundTripLeavesTheAllowListAsItFoundIt()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);
        File.WriteAllText(settings, """{ "permissions": { "allow": ["Bash(ls)"] } }""");

        ClaudePermissions.ApplyGrant(ClaudePermissions.PlanGrant(settings), sandbox.Home.GrantedPermissionsPath);
        ClaudePermissions.ApplyRevoke(
            ClaudePermissions.PlanRevoke(settings, sandbox.Home.GrantedPermissionsPath),
            sandbox.Home.GrantedPermissionsPath);

        Assert.Equal(["Bash(ls)"], AllowListOf(settings));
        Assert.False(File.Exists(sandbox.Home.GrantedPermissionsPath));
    }

    // Relaxed parsing would accept these and then drop them on the way back out, silently
    // deleting something the user wrote. Refusing is the smaller harm.
    [Theory]
    [InlineData("{ // my settings\n  \"permissions\": {} }")]
    [InlineData("{ \"permissions\": { \"allow\": [\"Bash(ls)\",] } }")]
    [InlineData("not json at all")]
    public void RefusesToRewriteSettingsItCannotParseStrictly(string content)
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);
        File.WriteAllText(settings, content);

        Assert.Throws<ClaudeSettingsException>(() => ClaudePermissions.PlanGrant(settings));
        Assert.Equal(content, File.ReadAllText(settings));
    }

    [Fact]
    public void RefusesASettingsFileWhoseRootIsNotAnObject()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);
        File.WriteAllText(settings, "[1, 2, 3]");

        Assert.Throws<ClaudeSettingsException>(() => ClaudePermissions.PlanGrant(settings));
    }

    [Fact]
    public void PlanningChangesNothingOnDisk()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);
        const string original = """{ "permissions": { "allow": [] } }""";
        File.WriteAllText(settings, original);

        var plan = ClaudePermissions.PlanGrant(settings);

        Assert.Equal(ClaudePermissions.GrantedTools.Count, plan.ToAdd.Count);
        Assert.Equal(original, File.ReadAllText(settings));
        Assert.False(File.Exists(sandbox.Home.GrantedPermissionsPath));
    }

    [Fact]
    public void AddsThePermissionsBlockWhenTheFileHasOtherKeysButNoPermissions()
    {
        using var sandbox = new SandboxHome();
        var settings = SettingsIn(sandbox);
        File.WriteAllText(settings, """{ "model": "opus" }""");

        ClaudePermissions.ApplyGrant(ClaudePermissions.PlanGrant(settings), sandbox.Home.GrantedPermissionsPath);

        Assert.Equal(ClaudePermissions.GrantedTools.ToHashSet(), AllowListOf(settings));
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
    }
}
