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
            "restart" => RestartCommand.Run(homePath, rest, stdout, stderr),
            "status" => StatusCommand.Run(homePath, rest, stdout, stderr),
            "doctor" => DoctorCommand.Run(homePath, rest, stdout, stderr),
            "hook" => HookCommand.Run(homePath, rest, stdout, stderr),
            "probe" => ProbeCommand.Run(homePath, rest, stdout, stderr),
            "permissions" => PermissionsCommand.Run(homePath, rest, stdout, stderr),
            "model" => ModelCommand.Run(homePath, rest, stdout, stderr),
            "embed" => EmbedCommand.Run(homePath, rest, stdout, stderr),
            "scan" => ScanCommand.Run(homePath, rest, stdout, stderr),
            "index" => IndexCommand.Run(homePath, rest, stdout, stderr),
            "explain" => ExplainCommand.Run(homePath, rest, stdout, stderr),
            "backup" => BackupCommand.Run(homePath, rest, stdout, stderr),
            "repo" => RepoCommand.Run(homePath, rest, stdout, stderr),
            "queue" => QueueCommand.Run(homePath, rest, stdout, stderr),
            "repair" => RepairCommand.Run(homePath, rest, stdout, stderr),
            "compact" => CompactCommand.Run(homePath, rest, stdout, stderr),
            "export" => ExportCommand.Run(homePath, rest, stdout, stderr),
            "import" => ImportCommand.Run(homePath, rest, stdout, stderr),
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
        writer.WriteLine("  init [--with-embeddings]          create the Engram home directory structure and default config");
        writer.WriteLine("  serve [--port <n>]                 run the MCP server over HTTP in the foreground");
        writer.WriteLine("  start [--port <n>]                 start the MCP server as a detached daemon");
        writer.WriteLine("  stop                               stop the running MCP server");
        writer.WriteLine("  restart [--port <n>]               stop the running server if any, then start one");
        writer.WriteLine("  status                             report whether the MCP server is running");
        writer.WriteLine("  doctor [options]                   check the whole instance, and say what to type about it");
        writer.WriteLine("  hook <event>                       hook entrypoint: session-start|subagent-start|pre-compact|user-prompt|file-touched");
        writer.WriteLine("  probe [options]                    summarize telemetry and store density for the M0 adoption probe");
        writer.WriteLine("  permissions [options]              allow Claude Code to call Engram's memory tools unprompted");
        writer.WriteLine("  model <list|install|path>          list the local embedding models, or download one");
        writer.WriteLine("  embed --status [--watch]           how far the vector index has got, and whether it is moving");
        writer.WriteLine("  embed --probe                      ask the configured endpoint how wide its vectors are");
        writer.WriteLine("  embed --rebuild                    discard the vector index and make it again from facts");
        writer.WriteLine();
        writer.WriteLine("embedding options:");
        writer.WriteLine("  init --with-embeddings             pick a provider: none, a local model, or an endpoint");
        writer.WriteLine("  init --provider <name> [...]       say it outright instead: none|local|openai-compat|ollama");
        writer.WriteLine("       --model --endpoint --dim --api-key-env --force");
        writer.WriteLine("                                     leave --dim off and the endpoint is asked for it");
        writer.WriteLine("  model install <id> --use-it        download a model and switch the config to it");
        writer.WriteLine("  embed --status [--watch]           progress, rate and what is being embedded; --watch redraws");
        writer.WriteLine("  embed --probe [--use-it]           report the width, and optionally write it to the config");
        writer.WriteLine("  embed --rebuild [--apply]          re-embed every live fact; needed after a model change");
        writer.WriteLine("  scan [path] [options]              report what indexing would read there, and what it would skip");
        writer.WriteLine("  index [path] [options]             turn a repo's files into code facts; incremental after the first run");
        writer.WriteLine("  explain <query> [options]          show why recall ranks what it ranks, and what it leaves out");
        writer.WriteLine("  backup <take|list|prune|restore|replay|import>  snapshot the store, or put the facts back");
        writer.WriteLine("  repo <enroll|decline|later|reset|list> [path]  record or inspect the decision to keep a checkout indexed");
        writer.WriteLine("  queue <status|compact>             report the file-touched edit queue, or fold it down");
        writer.WriteLine("  repair [--apply]                   rebuild derived state — the lexical index, denormalized paths — never facts");
        writer.WriteLine("  compact [--path <prefix>] [--apply]  prune regenerable code facts: closed ones, or a whole subtree with --path");
        writer.WriteLine("  export [--path <prefix>] [--out <file>]  write facts as a portable JSONL bundle (stdout by default)");
        writer.WriteLine("  import <file> [--apply]            add a bundle's facts; never rewrites or closes what the store already has");
        writer.WriteLine();
        writer.WriteLine("queue options:");
        writer.WriteLine("  compact [--apply]                  keep the newest entry per file and delete the rest");
        writer.WriteLine("  compact --apply --if-large         only when the queue has grown; what session start runs");
        writer.WriteLine();
        writer.WriteLine("backup options:");
        writer.WriteLine("  take --if-due                      snapshot only if the interval has passed and the store has changed");
        writer.WriteLine("  prune [--apply]                    thin old snapshots to the configured hourly/daily/weekly limits");
        writer.WriteLine("  restore [name] [--apply]           put a snapshot back, keeping the current store as a new snapshot");
        writer.WriteLine("  replay [file] [--apply]            read facts.jsonl into the store, adding what it does not have");
        writer.WriteLine("  import [dir] [--apply]             bring in user facts from the old user-facts/ JSON directory");
        writer.WriteLine();
        writer.WriteLine("repo options:");
        writer.WriteLine("  enroll [path]                       index this checkout now and keep it current as files change");
        writer.WriteLine("  decline [path]                      never offer to index this checkout again");
        writer.WriteLine("  later [path]                        ask again in a week instead of now");
        writer.WriteLine("  reset [path] [--apply]               forget the recorded decision, returning it to never-asked");
        writer.WriteLine("  list                                 every checkout with a recorded decision, and its indexing progress");
        writer.WriteLine();
        writer.WriteLine("doctor options (read-only; exit 1 only when something is broken):");
        writer.WriteLine("  --json                             emit the checks as JSON, for pasting into a bug report");
        writer.WriteLine("  --offline                          skip the one network call, to the embedding endpoint");
        writer.WriteLine("  --no-repo                          skip the indexing check for the current directory");
        writer.WriteLine();
        writer.WriteLine("scan options:");
        writer.WriteLine("  --skipped                          name every skipped file and why");
        writer.WriteLine("  --files                            name every file that would be indexed");
        writer.WriteLine();
        writer.WriteLine("index options (dry run unless --apply is given):");
        writer.WriteLine("  --apply                            actually write the facts and consume the queue");
        writer.WriteLine("  --drain                            index only what the file-touched queue names");
        writer.WriteLine("  --full                             ignore what was indexed before and re-read everything");
        writer.WriteLine("  --drain --apply --auto             what session start runs; declines silently unless configured and in a checkout");
        writer.WriteLine();
        writer.WriteLine("explain options:");
        writer.WriteLine("  --budget <n>                       token budget to test against (default 500)");
        writer.WriteLine("  --limit <n>                        candidates to print (default 20)");
        writer.WriteLine("  --session <id>                     treat this host session's notes as working memory");
        writer.WriteLine();
        writer.WriteLine("model subcommands:");
        writer.WriteLine("  list                               show every model, its size and tradeoff, and whether it is installed");
        writer.WriteLine("  install <id|default>               download and verify one model");
        writer.WriteLine("  path <id>                          print where that model lives");
        writer.WriteLine();
        writer.WriteLine("serve/start options:");
        writer.WriteLine("  --port <n>                         port to bind (default 7433, or $ENGRAM_PORT)");
        writer.WriteLine();
        writer.WriteLine("probe options:");
        writer.WriteLine("  --json                             emit the summary as a JSON object instead of text");
        writer.WriteLine("  --since <n>d                       only consider records from the last n days, e.g. --since 7d");
        writer.WriteLine();
        writer.WriteLine("permissions options (dry run unless --apply is given):");
        writer.WriteLine("  --apply                            actually edit the settings file");
        writer.WriteLine("  --remove                           take back only the entries Engram added");
        writer.WriteLine("  --settings <path>                  edit this settings file instead of Claude Code's user settings");
    }
}
