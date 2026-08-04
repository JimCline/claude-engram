using System.Text.Json.Nodes;
using Engram.Core;

namespace Engram.Cli;

internal static class ClaudeCodeFileWriter
{
    public static int Write(string settingsPath, JsonObject settings, string mcpConfigPath, JsonObject mcpConfig, bool dryRun, TextWriter stdout)
    {
        var settingsJson = JsonFileEditor.ToIndentedJson(settings);
        var mcpJson = JsonFileEditor.ToIndentedJson(mcpConfig);

        if (dryRun)
        {
            stdout.WriteLine(settingsPath);
            stdout.Write(settingsJson);
            stdout.WriteLine(mcpConfigPath);
            stdout.Write(mcpJson);
            return 0;
        }

        var settingsBackup = JsonFileEditor.WriteWithBackup(settingsPath, settingsJson);
        if (settingsBackup is not null)
        {
            stdout.WriteLine($"backup: {settingsBackup}");
        }

        stdout.WriteLine(settingsPath);

        var mcpBackup = JsonFileEditor.WriteWithBackup(mcpConfigPath, mcpJson);
        if (mcpBackup is not null)
        {
            stdout.WriteLine($"backup: {mcpBackup}");
        }

        stdout.WriteLine(mcpConfigPath);

        return 0;
    }
}
