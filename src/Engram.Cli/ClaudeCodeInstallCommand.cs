using Engram.Core;

namespace Engram.Cli;

internal static class ClaudeCodeInstallCommand
{
    public static int Run(string settingsPath, string mcpConfigPath, bool dryRun, string engramBinaryPath, TextWriter stdout, TextWriter stderr)
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

        try
        {
            ClaudeCodeSettingsEditor.ApplyInstall(settings, engramBinaryPath);
        }
        catch (ConfigShapeException ex)
        {
            stderr.WriteLine($"error: {settingsPath} has an unexpected value at '{ex.KeyPath}' and was not modified (found {ex.ActualNodeKind})");
            return 2;
        }

        try
        {
            ClaudeMcpConfigEditor.ApplyInstall(mcpConfig, engramBinaryPath);
        }
        catch (ConfigShapeException ex)
        {
            stderr.WriteLine($"error: {mcpConfigPath} has an unexpected value at '{ex.KeyPath}' and was not modified (found {ex.ActualNodeKind})");
            return 2;
        }

        return ClaudeCodeFileWriter.Write(settingsPath, settings, mcpConfigPath, mcpConfig, dryRun, stdout);
    }
}
