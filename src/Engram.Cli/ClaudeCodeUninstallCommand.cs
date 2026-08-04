using Engram.Core;

namespace Engram.Cli;

internal static class ClaudeCodeUninstallCommand
{
    public static int Run(string settingsPath, string mcpConfigPath, bool dryRun, TextWriter stdout, TextWriter stderr)
    {
        if (!JsonFileEditor.TryReadObject(settingsPath, out var settings, out var settingsError))
        {
            stderr.WriteLine($"error: {settingsPath} contains invalid JSON and was not modified ({settingsError})");
            return 2;
        }

        if (!JsonFileEditor.TryReadObject(mcpConfigPath, out var mcpConfig, out var mcpError))
        {
            stderr.WriteLine($"error: {mcpConfigPath} contains invalid JSON and was not modified ({mcpError})");
            return 2;
        }

        ClaudeCodeSettingsEditor.ApplyUninstall(settings);
        ClaudeMcpConfigEditor.ApplyUninstall(mcpConfig);

        return ClaudeCodeFileWriter.Write(settingsPath, settings, mcpConfigPath, mcpConfig, dryRun, stdout);
    }
}
