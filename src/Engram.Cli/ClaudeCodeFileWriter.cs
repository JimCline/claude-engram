using System.Text.Json.Nodes;
using Engram.Core;

namespace Engram.Cli;

internal static class ClaudeCodeFileWriter
{
    public static int Write(
        string settingsPath,
        JsonObject settings,
        JsonFileEditor.FileFreshness settingsFreshness,
        string mcpConfigPath,
        JsonObject mcpConfig,
        JsonFileEditor.FileFreshness mcpFreshness,
        bool dryRun,
        TextWriter stdout,
        TextWriter stderr)
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

        PendingWrite settingsWrite;

        try
        {
            settingsWrite = AtomicFile.Prepare(settingsPath, settingsJson);
        }
        catch (IOException ex)
        {
            stderr.WriteLine($"error: could not prepare {settingsPath} for writing: {ex.Message}");
            return 2;
        }

        using var settingsWriteScope = settingsWrite;

        PendingWrite mcpWrite;

        try
        {
            mcpWrite = AtomicFile.Prepare(mcpConfigPath, mcpJson);
        }
        catch (IOException ex)
        {
            stderr.WriteLine($"error: could not prepare {mcpConfigPath} for writing: {ex.Message}");
            return 2;
        }

        using var mcpWriteScope = mcpWrite;

        if (!settingsFreshness.IsUnchanged(settingsPath) || !mcpFreshness.IsUnchanged(mcpConfigPath))
        {
            stderr.WriteLine("error: the settings or MCP config file changed while it was being edited; no changes were made");
            return 2;
        }

        string? settingsBackup;
        string? mcpBackup;

        try
        {
            settingsBackup = File.Exists(settingsPath) ? JsonFileEditor.CreateBackup(settingsPath) : null;
            mcpBackup = File.Exists(mcpConfigPath) ? JsonFileEditor.CreateBackup(mcpConfigPath) : null;
        }
        catch (IOException ex)
        {
            stderr.WriteLine($"error: could not create a backup: {ex.Message}");
            return 2;
        }

        try
        {
            settingsWrite.Commit();
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: could not update {settingsPath} ({ex.Message}); no changes were made");
            return 2;
        }

        try
        {
            mcpWrite.Commit();
        }
        catch (Exception ex)
        {
            stderr.WriteLine(
                $"error: {settingsPath} was updated but {mcpConfigPath} was not ({ex.Message}); re-run install to retry the MCP config update");
            return 2;
        }

        if (settingsBackup is not null)
        {
            stdout.WriteLine($"backup: {settingsBackup}");
        }

        stdout.WriteLine(settingsPath);

        if (mcpBackup is not null)
        {
            stdout.WriteLine($"backup: {mcpBackup}");
        }

        stdout.WriteLine(mcpConfigPath);

        return 0;
    }
}
