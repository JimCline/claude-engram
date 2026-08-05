using System.Text.Json;
using Engram.Core;

namespace Engram.Cli;

internal static class HookCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 1)
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        var eventName = rest[0];
        if (eventName is not ("session-start" or "subagent-start" or "pre-compact" or "file-touched"))
        {
            return Usage(stderr);
        }

        EngramHome home;
        try
        {
            home = EngramHome.ResolveFromProcess(homePath);
        }
        catch
        {
            return 0;
        }

        if (!File.Exists(home.ConfigPath))
        {
            return 0;
        }

        return eventName switch
        {
            // Switch arms evaluate lazily, so file-touched never pays to drain stdin —
            // its payload carries the whole tool_input, which for a Write is an entire
            // file, and its budget is 10ms unconditionally.
            "session-start" => RunSessionStart(home, stdout, ReadPayload()),
            "subagent-start" => RunSubagentStart(home, stdout, ReadPayload()),
            "pre-compact" => RunPreCompact(home, ReadPayload()),
            "file-touched" => RunFileTouched(home),
            _ => 0,
        };
    }

    private static int Usage(TextWriter stderr)
    {
        CliApp.PrintUsage(stderr);
        return 1;
    }

    private static int RunSessionStart(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        var sessionId = ResolveSessionId(payload);
        var primer = PrimerBuilder.Build(CannedFacts.All);

        try
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.SessionStart));
        }
        catch
        {
        }

        WriteJson(stdout, HookJsonContext.Default.AdditionalContextHookOutput,
            new AdditionalContextHookOutput(new HookSpecificOutput("SessionStart", primer)));
        return 0;
    }

    // SessionStart never fires for a subagent, and SubagentStart reaches spawn paths a
    // PreToolUse rewrite of the Agent tool structurally cannot — a measured 47-agent
    // workflow run produced zero relay events through that route. This is the only proven
    // channel to every spawn.
    //
    // Bare stdout is SILENTLY DISCARDED on this event. SessionStart accepts it, so the
    // habit formed there actively misleads here; all three keys below are load-bearing and
    // hookEventName must match the event. There is no error when this is wrong — the
    // primer simply never arrives, which is indistinguishable from a subagent ignoring it.
    private static int RunSubagentStart(EngramHome home, TextWriter stdout, HookStdinInput? payload)
    {
        var primer = PrimerBuilder.BuildForSubagent(CannedFacts.All);

        try
        {
            // Recording the session id the subagent was handed answers, from the first real
            // probe run, whether it matches its parent's. If it does, session facts are
            // shared across the spawn with no further work; if not, D11's sharing needs a
            // parent id threaded through instead of being assumed.
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: ResolveSessionId(payload),
                Kind: TelemetryEventKind.SubagentStart,
                AgentId: payload?.AgentId,
                AgentType: payload?.AgentType));
        }
        catch
        {
        }

        WriteJson(stdout, HookJsonContext.Default.AdditionalContextHookOutput,
            new AdditionalContextHookOutput(new HookSpecificOutput("SubagentStart", primer)));
        return 0;
    }

    private static int RunPreCompact(EngramHome home, HookStdinInput? payload)
    {
        var sessionId = ResolveSessionId(payload);

        try
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: sessionId,
                Kind: TelemetryEventKind.PreCompact));
        }
        catch
        {
        }

        return 0;
    }

    // Claude Code pipes a JSON payload with a "session_id" field on the hook's stdin
    // (https://code.claude.com/docs/en/hooks). Only read it when stdin is actually
    // redirected, so a plain interactive invocation never blocks waiting on a terminal.
    //
    // Read once and passed down rather than fetched where needed: a stream drains once,
    // and a second caller would silently get nothing. Caching it in a static would fix
    // that but leak one invocation's payload into the next inside a test process.
    private static HookStdinInput? ReadPayload()
    {
        if (!Console.IsInputRedirected)
        {
            return null;
        }

        try
        {
            var input = Console.In.ReadToEnd();
            return string.IsNullOrWhiteSpace(input)
                ? null
                : JsonSerializer.Deserialize(input, HookJsonContext.Default.HookStdinInput);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSessionId(HookStdinInput? payload) =>
        payload?.SessionId is { Length: > 0 } sessionId ? sessionId : Guid.NewGuid().ToString("N");

    private static int RunFileTouched(EngramHome home)
    {
        try
        {
            Directory.CreateDirectory(home.QueueDir);

            var now = DateTime.UtcNow;
            var spoolFileName = $"{now.Ticks}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}.spool";
            var spoolPath = Path.Combine(home.QueueDir, spoolFileName);

            using var stream = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(now.ToString("o"));
        }
        catch
        {
        }

        return 0;
    }

    private static void WriteJson<T>(TextWriter stdout, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, T value)
    {
        stdout.Write(JsonSerializer.Serialize(value, typeInfo));
        stdout.Write('\n');
    }
}
