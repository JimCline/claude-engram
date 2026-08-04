using System.Text.Json.Nodes;
using Engram.Cli;

namespace Engram.Integration.Tests;

public class ClaudeCodeInstallUninstallTests
{
    private const string EngramBinaryPath = "/usr/local/bin/engram";

    [Fact]
    public void Install_IntoNonExistentSettingsFile_CreatesExactlyTheThreeHookEvents()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "nested", "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        var exitCode = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(settingsPath));

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settingsPath))!;
        Assert.Equal(["hooks"], root.Select(kvp => kvp.Key));

        var hooks = (JsonObject)root["hooks"]!;
        Assert.Equal(
            new HashSet<string> { "SessionStart", "PreCompact", "PostToolUse" },
            hooks.Select(kvp => kvp.Key).ToHashSet());
    }

    [Fact]
    public void Install_PreservesUnrelatedTopLevelKeysAndForeignSessionStartEntry()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        File.WriteAllText(settingsPath, """
            {
              "theme": "dark",
              "model": "custom-model",
              "hooks": {
                "SessionStart": [
                  { "hooks": [ { "type": "command", "command": "other-tool init" } ] }
                ]
              }
            }
            """);

        var exitCode = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(settingsPath))!;
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.Equal("custom-model", root["model"]!.GetValue<string>());

        var sessionStartGroups = (JsonArray)root["hooks"]!["SessionStart"]!;
        Assert.Equal(2, sessionStartGroups.Count);

        var commands = sessionStartGroups
            .SelectMany(group => (JsonArray)group!["hooks"]!)
            .Select(entry => entry!["command"]!.GetValue<string>())
            .ToList();

        Assert.Contains("other-tool init", commands);
        Assert.Contains($"{EngramBinaryPath} hook session-start", commands);
    }

    [Fact]
    public void Install_Twice_ProducesByteIdenticalOutput()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        File.WriteAllText(settingsPath, """{ "theme": "dark" }""");
        File.WriteAllText(mcpPath, """{ "mcpServers": { "other-server": { "command": "other-mcp" } } }""");

        var firstExit = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());
        Assert.Equal(0, firstExit);

        var settingsAfterFirst = File.ReadAllText(settingsPath);
        var mcpAfterFirst = File.ReadAllText(mcpPath);

        var secondExit = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());
        Assert.Equal(0, secondExit);

        Assert.Equal(settingsAfterFirst, File.ReadAllText(settingsPath));
        Assert.Equal(mcpAfterFirst, File.ReadAllText(mcpPath));
    }

    [Fact]
    public void InstallThenUninstall_SettingsRoundTrip_IsSemanticallyEqualToOriginal()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string original = """
            {
              "theme": "dark",
              "model": "custom-model",
              "permissions": { "allow": [ "Bash(git *)" ] },
              "hooks": {
                "SessionStart": [
                  { "hooks": [ { "type": "command", "command": "other-tool session-init" } ] }
                ],
                "PostToolUse": [
                  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "linter run" } ] }
                ]
              }
            }
            """;
        File.WriteAllText(settingsPath, original);

        var installExit = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());
        Assert.Equal(0, installExit);

        var uninstallExit = ClaudeCodeUninstallCommand.Run(settingsPath, mcpPath, dryRun: false, new StringWriter(), new StringWriter());
        Assert.Equal(0, uninstallExit);

        var originalNode = JsonNode.Parse(original);
        var roundTrippedNode = JsonNode.Parse(File.ReadAllText(settingsPath));

        Assert.True(
            JsonNode.DeepEquals(originalNode, roundTrippedNode),
            $"expected:\n{originalNode!.ToJsonString()}\nactual:\n{roundTrippedNode!.ToJsonString()}");
    }

    [Fact]
    public void InstallThenUninstall_McpConfigRoundTrip_PreservesOtherServer()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string original = """
            {
              "mcpServers": {
                "other-server": { "command": "other-mcp", "args": [ "serve" ] }
              },
              "unrelatedTopLevel": true
            }
            """;
        File.WriteAllText(mcpPath, original);

        var installExit = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());
        Assert.Equal(0, installExit);

        var afterInstall = (JsonObject)JsonNode.Parse(File.ReadAllText(mcpPath))!;
        Assert.True(afterInstall["mcpServers"]!.AsObject().ContainsKey("engram"));
        Assert.True(afterInstall["mcpServers"]!.AsObject().ContainsKey("other-server"));

        var uninstallExit = ClaudeCodeUninstallCommand.Run(settingsPath, mcpPath, dryRun: false, new StringWriter(), new StringWriter());
        Assert.Equal(0, uninstallExit);

        var originalNode = JsonNode.Parse(original);
        var roundTrippedNode = JsonNode.Parse(File.ReadAllText(mcpPath));

        Assert.True(
            JsonNode.DeepEquals(originalNode, roundTrippedNode),
            $"expected:\n{originalNode!.ToJsonString()}\nactual:\n{roundTrippedNode!.ToJsonString()}");
    }

    [Fact]
    public void DryRun_WritesNothing_AndPrintsValidJson()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string originalSettings = """{ "theme": "dark" }""";
        const string originalMcp = """{ "mcpServers": {} }""";
        File.WriteAllText(settingsPath, originalSettings);
        File.WriteAllText(mcpPath, originalMcp);

        var settingsWriteTimeBefore = File.GetLastWriteTimeUtc(settingsPath);
        var mcpWriteTimeBefore = File.GetLastWriteTimeUtc(mcpPath);

        var stdout = new StringWriter();
        var exitCode = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: true, EngramBinaryPath, stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(originalSettings, File.ReadAllText(settingsPath));
        Assert.Equal(originalMcp, File.ReadAllText(mcpPath));
        Assert.Equal(settingsWriteTimeBefore, File.GetLastWriteTimeUtc(settingsPath));
        Assert.Equal(mcpWriteTimeBefore, File.GetLastWriteTimeUtc(mcpPath));

        var jsonBlocks = ExtractJsonBlocks(stdout.ToString());
        Assert.Equal(2, jsonBlocks.Count);
        foreach (var block in jsonBlocks)
        {
            var parsed = JsonNode.Parse(block);
            Assert.NotNull(parsed);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingInvalidJson_IsNotModified_AndExitsWithRuntimeFailure(bool invalidIsSettings)
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string invalidJson = "{ this is not valid json ";
        const string validJson = "{}";

        File.WriteAllText(settingsPath, invalidIsSettings ? invalidJson : validJson);
        File.WriteAllText(mcpPath, invalidIsSettings ? validJson : invalidJson);

        var stderr = new StringWriter();
        var exitCode = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), stderr);

        Assert.Equal(2, exitCode);
        Assert.NotEqual(string.Empty, stderr.ToString());
        Assert.Equal(invalidIsSettings ? invalidJson : validJson, File.ReadAllText(settingsPath));
        Assert.Equal(invalidIsSettings ? validJson : invalidJson, File.ReadAllText(mcpPath));
    }

    [Fact]
    public void Install_CreatesBackupFile_AndSecondInstallProducesDashTwo()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string original = """{ "theme": "dark" }""";
        File.WriteAllText(settingsPath, original);

        var firstBackupPath = $"{settingsPath}.engram-backup-1";
        var secondBackupPath = $"{settingsPath}.engram-backup-2";
        Assert.False(File.Exists(firstBackupPath));

        var firstExit = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());
        Assert.Equal(0, firstExit);

        Assert.True(File.Exists(firstBackupPath));
        Assert.Equal(original, File.ReadAllText(firstBackupPath));
        Assert.False(File.Exists(secondBackupPath));

        var secondExit = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());
        Assert.Equal(0, secondExit);

        Assert.True(File.Exists(secondBackupPath));
    }

    [Fact]
    public void Install_HooksPresentAsString_ExitsWithoutModifyingOrBackingUp()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string originalSettings = """{ "theme": "dark", "hooks": "not-an-object" }""";
        const string originalMcp = "{}";
        File.WriteAllText(settingsPath, originalSettings);
        File.WriteAllText(mcpPath, originalMcp);

        var stderr = new StringWriter();
        var exitCode = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), stderr);

        Assert.Equal(2, exitCode);
        Assert.NotEqual(string.Empty, stderr.ToString());
        Assert.Equal(originalSettings, File.ReadAllText(settingsPath));
        Assert.Equal(originalMcp, File.ReadAllText(mcpPath));
        Assert.False(File.Exists($"{settingsPath}.engram-backup-1"));
        Assert.False(File.Exists($"{mcpPath}.engram-backup-1"));
    }

    [Fact]
    public void Install_HooksPresentAsArray_ExitsWithoutModifyingOrBackingUp()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string originalSettings = """{ "theme": "dark", "hooks": [] }""";
        const string originalMcp = "{}";
        File.WriteAllText(settingsPath, originalSettings);
        File.WriteAllText(mcpPath, originalMcp);

        var stderr = new StringWriter();
        var exitCode = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), stderr);

        Assert.Equal(2, exitCode);
        Assert.NotEqual(string.Empty, stderr.ToString());
        Assert.Equal(originalSettings, File.ReadAllText(settingsPath));
        Assert.Equal(originalMcp, File.ReadAllText(mcpPath));
        Assert.False(File.Exists($"{settingsPath}.engram-backup-1"));
        Assert.False(File.Exists($"{mcpPath}.engram-backup-1"));
    }

    [Fact]
    public void Install_HooksSessionStartPresentAsObject_ExitsWithoutModifyingOrBackingUp()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string originalSettings = """
            {
              "hooks": {
                "SessionStart": { "not": "an-array" }
              }
            }
            """;
        const string originalMcp = "{}";
        File.WriteAllText(settingsPath, originalSettings);
        File.WriteAllText(mcpPath, originalMcp);

        var stderr = new StringWriter();
        var exitCode = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), stderr);

        Assert.Equal(2, exitCode);
        Assert.NotEqual(string.Empty, stderr.ToString());
        Assert.Equal(originalSettings, File.ReadAllText(settingsPath));
        Assert.Equal(originalMcp, File.ReadAllText(mcpPath));
        Assert.False(File.Exists($"{settingsPath}.engram-backup-1"));
        Assert.False(File.Exists($"{mcpPath}.engram-backup-1"));
    }

    [Fact]
    public void Install_McpServersPresentAsString_ExitsWithoutModifyingOrBackingUp()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string originalSettings = """{ "theme": "dark" }""";
        const string originalMcp = """{ "mcpServers": "not-an-object" }""";
        File.WriteAllText(settingsPath, originalSettings);
        File.WriteAllText(mcpPath, originalMcp);

        var stderr = new StringWriter();
        var exitCode = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), stderr);

        Assert.Equal(2, exitCode);
        Assert.NotEqual(string.Empty, stderr.ToString());
        Assert.Equal(originalSettings, File.ReadAllText(settingsPath));
        Assert.Equal(originalMcp, File.ReadAllText(mcpPath));
        Assert.False(File.Exists($"{settingsPath}.engram-backup-1"));
        Assert.False(File.Exists($"{mcpPath}.engram-backup-1"));
    }

    [Fact]
    public void InstallThenUninstall_SettingsWithNoHooksKey_RoundTripsToOriginal()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string original = """
            {
              "theme": "dark",
              "model": "custom-model"
            }
            """;
        File.WriteAllText(settingsPath, original);

        var installExit = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());
        Assert.Equal(0, installExit);

        var uninstallExit = ClaudeCodeUninstallCommand.Run(settingsPath, mcpPath, dryRun: false, new StringWriter(), new StringWriter());
        Assert.Equal(0, uninstallExit);

        var originalNode = JsonNode.Parse(original);
        var roundTrippedNode = JsonNode.Parse(File.ReadAllText(settingsPath));

        Assert.True(
            JsonNode.DeepEquals(originalNode, roundTrippedNode),
            $"expected:\n{originalNode!.ToJsonString()}\nactual:\n{roundTrippedNode!.ToJsonString()}");
    }

    [Fact]
    public void InstallThenUninstall_McpConfigWithNoMcpServersKey_RoundTripsToOriginal()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string original = """
            {
              "unrelatedTopLevel": true
            }
            """;
        File.WriteAllText(mcpPath, original);

        var installExit = ClaudeCodeInstallCommand.Run(settingsPath, mcpPath, dryRun: false, EngramBinaryPath, new StringWriter(), new StringWriter());
        Assert.Equal(0, installExit);

        var uninstallExit = ClaudeCodeUninstallCommand.Run(settingsPath, mcpPath, dryRun: false, new StringWriter(), new StringWriter());
        Assert.Equal(0, uninstallExit);

        var originalNode = JsonNode.Parse(original);
        var roundTrippedNode = JsonNode.Parse(File.ReadAllText(mcpPath));

        Assert.True(
            JsonNode.DeepEquals(originalNode, roundTrippedNode),
            $"expected:\n{originalNode!.ToJsonString()}\nactual:\n{roundTrippedNode!.ToJsonString()}");
    }

    [Fact]
    public void Uninstall_ForeignHookContainingEngramHookSubstring_SurvivesUntouched()
    {
        using var dir = new TempDirectory();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var mcpPath = Path.Combine(dir.Path, "mcp.json");

        const string original = """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [ { "type": "command", "command": "/opt/other/my-engram hook something-else" } ] }
                ]
              }
            }
            """;
        File.WriteAllText(settingsPath, original);

        var uninstallExit = ClaudeCodeUninstallCommand.Run(settingsPath, mcpPath, dryRun: false, new StringWriter(), new StringWriter());
        Assert.Equal(0, uninstallExit);

        var originalNode = JsonNode.Parse(original);
        var roundTrippedNode = JsonNode.Parse(File.ReadAllText(settingsPath));

        Assert.True(
            JsonNode.DeepEquals(originalNode, roundTrippedNode),
            $"expected:\n{originalNode!.ToJsonString()}\nactual:\n{roundTrippedNode!.ToJsonString()}");
    }

    private static List<string> ExtractJsonBlocks(string text)
    {
        var blocks = new List<string>();
        var depth = 0;
        var start = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                if (depth == 0)
                {
                    start = i;
                }

                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    blocks.Add(text[start..(i + 1)]);
                    start = -1;
                }
            }
        }

        return blocks;
    }
}
