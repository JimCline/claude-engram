using Engram.Core;

namespace Engram.Cli;

public static class CliApp
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var remaining = new List<string>();
        string? homePath = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--home")
            {
                if (i + 1 >= args.Length)
                {
                    stderr.WriteLine("error: --home requires a value");
                    return 1;
                }

                homePath = args[++i];
                continue;
            }

            remaining.Add(args[i]);
        }

        if (remaining.Count == 0)
        {
            PrintUsage(stderr);
            return 1;
        }

        var rest = remaining.Skip(1).ToArray();

        return remaining[0] switch
        {
            "home" => HomeCommand.Run(homePath, rest, stdout, stderr),
            "init" => InitCommand.Run(homePath, rest, stdout, stderr),
            "install" => RunClaudeCodeVerb(rest, install: true, stdout, stderr),
            "uninstall" => RunClaudeCodeVerb(rest, install: false, stdout, stderr),
            "mcp" => McpCommand.Run(homePath, rest, stdout, stderr),
            "hook" => HookCommand.Run(homePath, rest, stdout, stderr),
            _ => Usage(stderr),
        };
    }

    private static int Usage(TextWriter stderr)
    {
        PrintUsage(stderr);
        return 1;
    }

    private static int RunClaudeCodeVerb(string[] rest, bool install, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length == 0 || rest[0] != "claude-code")
        {
            PrintUsage(stderr);
            return 1;
        }

        string? settingsPath = null;
        string? mcpConfigPath = null;
        var dryRun = false;

        for (var i = 1; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--settings-path":
                    if (i + 1 >= rest.Length)
                    {
                        stderr.WriteLine("error: --settings-path requires a value");
                        return 1;
                    }

                    settingsPath = rest[++i];
                    break;

                case "--mcp-config":
                    if (i + 1 >= rest.Length)
                    {
                        stderr.WriteLine("error: --mcp-config requires a value");
                        return 1;
                    }

                    mcpConfigPath = rest[++i];
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                default:
                    PrintUsage(stderr);
                    return 1;
            }
        }

        var userProfileDirectory = EngramHome.UserProfileDirectory();
        settingsPath ??= ClaudeCodeDefaults.SettingsPath(userProfileDirectory);
        mcpConfigPath ??= ClaudeCodeDefaults.McpConfigPath(userProfileDirectory);

        if (Environment.ProcessPath is not { } engramBinaryPath)
        {
            stderr.WriteLine("error: could not determine the path of the running engram executable");
            return 2;
        }

        return install
            ? ClaudeCodeInstallCommand.Run(settingsPath, mcpConfigPath, dryRun, engramBinaryPath, stdout, stderr)
            : ClaudeCodeUninstallCommand.Run(settingsPath, mcpConfigPath, dryRun, stdout, stderr);
    }

    internal static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("usage: engram [--home <path>] <command> [options]");
        writer.WriteLine();
        writer.WriteLine("commands:");
        writer.WriteLine("  home                              print resolved Engram home paths");
        writer.WriteLine("  init                              create the Engram home directory structure and default config");
        writer.WriteLine("  install claude-code [options]     install Claude Code hooks and MCP server registration");
        writer.WriteLine("  uninstall claude-code [options]   remove Claude Code hooks and MCP server registration");
        writer.WriteLine("  mcp                                run the MCP server on stdio");
        writer.WriteLine("  hook <event>                       hook entrypoint: session-start|pre-compact|file-touched");
        writer.WriteLine();
        writer.WriteLine("install/uninstall claude-code options:");
        writer.WriteLine("  --settings-path <file>            settings file to modify (defaults to the user's Claude Code settings)");
        writer.WriteLine("  --mcp-config <file>                JSON file holding MCP server registrations");
        writer.WriteLine("  --dry-run                          print the resulting JSON without writing anything");
    }
}
