namespace Engram.Core;

public static class ClaudeCodeDefaults
{
    public static string SettingsPath(string userProfileDirectory) =>
        Path.Combine(userProfileDirectory, ".claude", "settings.json");

    public static string McpConfigPath(string userProfileDirectory) =>
        Path.Combine(userProfileDirectory, ".claude.json");
}
