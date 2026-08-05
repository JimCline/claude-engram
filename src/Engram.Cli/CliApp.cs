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
            "serve" => ServeCommand.Run(homePath, rest, stdout, stderr),
            "start" => StartCommand.Run(homePath, rest, stdout, stderr),
            "stop" => StopCommand.Run(homePath, rest, stdout, stderr),
            "status" => StatusCommand.Run(homePath, rest, stdout, stderr),
            "hook" => HookCommand.Run(homePath, rest, stdout, stderr),
            "probe" => ProbeCommand.Run(homePath, rest, stdout, stderr),
            _ => Usage(stderr),
        };
    }

    private static int Usage(TextWriter stderr)
    {
        PrintUsage(stderr);
        return 1;
    }

    internal static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("usage: engram [--home <path>] <command> [options]");
        writer.WriteLine();
        writer.WriteLine("commands:");
        writer.WriteLine("  home                              print resolved Engram home paths");
        writer.WriteLine("  init                              create the Engram home directory structure and default config");
        writer.WriteLine("  serve [--port <n>]                 run the MCP server over HTTP in the foreground");
        writer.WriteLine("  start [--port <n>]                 start the MCP server as a detached daemon");
        writer.WriteLine("  stop                               stop the running MCP server");
        writer.WriteLine("  status                             report whether the MCP server is running");
        writer.WriteLine("  hook <event>                       hook entrypoint: session-start|pre-compact|file-touched");
        writer.WriteLine("  probe [options]                    summarize telemetry.jsonl for the M0 adoption probe");
        writer.WriteLine();
        writer.WriteLine("serve/start options:");
        writer.WriteLine("  --port <n>                         port to bind (default 7433, or $ENGRAM_PORT)");
        writer.WriteLine();
        writer.WriteLine("probe options:");
        writer.WriteLine("  --json                             emit the summary as a JSON object instead of text");
        writer.WriteLine("  --since <n>d                       only consider records from the last n days, e.g. --since 7d");
    }
}
