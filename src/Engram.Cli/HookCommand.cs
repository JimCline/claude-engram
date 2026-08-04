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

        return rest[0] switch
        {
            "session-start" => RunSessionStart(homePath, stdout),
            "pre-compact" => RunPreCompact(homePath),
            "file-touched" => RunFileTouched(homePath),
            _ => Usage(stderr),
        };
    }

    private static int Usage(TextWriter stderr)
    {
        CliApp.PrintUsage(stderr);
        return 1;
    }

    private static int RunSessionStart(string? homePath, TextWriter stdout)
    {
        var primer = PrimerBuilder.Build(CannedFacts.All);

        try
        {
            var home = EngramHome.ResolveFromProcess(homePath);
            var sessionId = Telemetry.OpenSession(home);
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

    private static int RunPreCompact(string? homePath)
    {
        try
        {
            var home = EngramHome.ResolveFromProcess(homePath);
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: Telemetry.CurrentSessionId(home),
                Kind: TelemetryEventKind.PreCompact));
        }
        catch
        {
        }

        return 0;
    }

    private static int RunFileTouched(string? homePath)
    {
        try
        {
            var home = EngramHome.ResolveFromProcess(homePath);
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
