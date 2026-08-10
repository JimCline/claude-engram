using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Drives the published AOT binary, because this command's whole job is rewriting a JSON file
/// through <c>JsonNode</c>, and reflection-shaped JSON is exactly the class of thing that works
/// under the JIT build and fails once trimmed.
///
/// Every test passes --settings, so nothing here can reach the real Claude Code settings file.
/// </summary>
public class PermissionsCommandTests
{
    private static string SettingsPathIn(TestHome home) => Path.Combine(home.Root, "settings.json");

    private static HashSet<string> AllowListOf(string settingsPath)
    {
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settingsPath))!;
        var allow = (JsonArray)root["permissions"]!["allow"]!;
        return [.. allow.Select(entry => entry!.GetValue<string>())];
    }

    [Fact]
    public void DryRunPrintsThePlanAndWritesNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var settings = SettingsPathIn(home);

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "permissions", "--settings", settings);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("would add", stdout);
        Assert.Contains("mcp__plugin_engram_engram__engram_recall", stdout);
        Assert.Contains("Dry run only", stdout);
        Assert.False(File.Exists(settings));
    }

    [Fact]
    public void ApplyGrantsTheReadAndWriteToolsAndNothingThatCloses()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var settings = SettingsPathIn(home);

        var (exitCode, stdout, _) = EngramProcess.Run(
            home.Root, "permissions", "--settings", settings, "--apply");

        Assert.Equal(0, exitCode);
        Assert.Contains("Granted 5 tool(s).", stdout);

        var allowed = AllowListOf(settings);
        Assert.Equal(5, allowed.Count);
        Assert.Contains("mcp__plugin_engram_engram__engram_recall", allowed);
        Assert.Contains("mcp__plugin_engram_engram__engram_remember", allowed);
        Assert.Contains("mcp__plugin_engram_engram__engram_status", allowed);
        Assert.Contains("mcp__plugin_engram_engram__engram_browse", allowed);
        Assert.Contains("mcp__plugin_engram_engram__engram_expand", allowed);

        // Closing a belief costs a confirmation prompt, so neither retraction nor
        // revision may ride the unprompted grant.
        Assert.DoesNotContain("mcp__plugin_engram_engram__engram_forget", allowed);
        Assert.DoesNotContain("mcp__plugin_engram_engram__engram_revise", allowed);
    }

    [Fact]
    public void GrantAndRevokeLeaveTheUsersOwnEntriesExactlyAsTheyWere()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var settings = SettingsPathIn(home);
        File.WriteAllText(
            settings,
            """
            {
              "model": "opus",
              "permissions": {
                "allow": ["Bash(git status)", "mcp__plugin_engram_engram__engram_status"]
              }
            }
            """);

        // The user's two entries plus the grant, minus the tool they already had.
        var (grantExit, _, _) = EngramProcess.Run(home.Root, "permissions", "--settings", settings, "--apply");
        Assert.Equal(0, grantExit);
        Assert.Equal(6, AllowListOf(settings).Count);

        var (revokeExit, revokeOut, _) = EngramProcess.Run(
            home.Root, "permissions", "--settings", settings, "--remove", "--apply");

        Assert.Equal(0, revokeExit);
        Assert.Contains("left alone", revokeOut);

        // engram_status was the user's before we ran, so it survives the uninstall.
        Assert.Equal(
            ["Bash(git status)", "mcp__plugin_engram_engram__engram_status"],
            AllowListOf(settings).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
    }

    // Granting and immediately revoking is what "install, change your mind, uninstall" looks
    // like, and the backup timestamp only resolves to the second. This collided and took the
    // AOT binary down with SIGABRT rather than reporting anything.
    [Fact]
    public void KeepsBothBackupsWhenGrantAndRevokeLandInTheSameSecond()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var settings = SettingsPathIn(home);
        File.WriteAllText(settings, """{ "permissions": { "allow": [] } }""");

        Assert.Equal(0, EngramProcess.Run(home.Root, "permissions", "--settings", settings, "--apply").ExitCode);
        var (revokeExit, _, revokeErr) = EngramProcess.Run(
            home.Root, "permissions", "--settings", settings, "--remove", "--apply");

        Assert.Equal(0, revokeExit);
        Assert.Equal(string.Empty, revokeErr);
        Assert.Equal(2, Directory.GetFiles(home.Root, "settings.json.engram-backup-*").Length);
        Assert.Empty(AllowListOf(settings));
    }

    [Fact]
    public void RefusesSettingsItCannotParseAndPrintsWhatToAddByHand()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var settings = SettingsPathIn(home);
        const string original = "{ \"permissions\": {} // trailing comment\n}";
        File.WriteAllText(settings, original);

        var (exitCode, _, stderr) = EngramProcess.Run(
            home.Root, "permissions", "--settings", settings, "--apply");

        Assert.Equal(1, exitCode);
        Assert.Contains("not strict JSON", stderr);
        Assert.Contains("mcp__plugin_engram_engram__engram_recall", stderr);
        Assert.Equal(original, File.ReadAllText(settings));
    }

    [Fact]
    public void BacksUpTheSettingsFileBeforeChangingIt()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();
        var settings = SettingsPathIn(home);
        const string original = """{ "permissions": { "allow": [] } }""";
        File.WriteAllText(settings, original);

        var (exitCode, stdout, _) = EngramProcess.Run(
            home.Root, "permissions", "--settings", settings, "--apply");

        Assert.Equal(0, exitCode);
        Assert.Contains("Backed up", stdout);

        var backups = Directory.GetFiles(home.Root, "settings.json.engram-backup-*");
        Assert.Equal(original, File.ReadAllText(Assert.Single(backups)));
    }
}
