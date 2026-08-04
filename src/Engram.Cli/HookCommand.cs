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
        if (eventName is not ("session-start" or "pre-compact" or "file-touched"))
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
            "session-start" => RunSessionStart(home, stdout),
            "pre-compact" => RunPreCompact(home),
            "file-touched" => RunFileTouched(home),
            _ => 0,
        };
    }

    private static int Usage(TextWriter stderr)
    {
        CliApp.PrintUsage(stderr);
        return 1;
    }

    private static int RunSessionStart(EngramHome home, TextWriter stdout)
    {
        var sessionId = ResolveSessionId();
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

        WriteJson(stdout, HookJsonContext.Default.SessionStartHookOutput,
            new SessionStartHookOutput(new HookSpecificOutput("SessionStart", primer)));
        return 0;
    }

    private static int RunPreCompact(EngramHome home)
    {
        var sessionId = ResolveSessionId();

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
    private static string ResolveSessionId()
    {
        if (Console.IsInputRedirected)
        {
            try
            {
                var input = Console.In.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(input))
                {
                    var payload = JsonSerializer.Deserialize(input, HookJsonContext.Default.HookStdinInput);
                    if (payload?.SessionId is { Length: > 0 } sessionId)
                    {
                        return sessionId;
                    }
                }
            }
            catch
            {
            }
        }

        return Guid.NewGuid().ToString("N");
    }

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
