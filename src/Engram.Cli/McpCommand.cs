using Engram.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Engram.Cli;

internal static class McpCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 0)
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var session = new McpSessionId(Guid.NewGuid().ToString("N"));

        Telemetry.Append(home, new TelemetryRecord(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: session.Value,
            Kind: TelemetryEventKind.ServerStart));

        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(home);
        builder.Services.AddSingleton(session);
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<EngramMcpTools>();

        builder.Build().RunAsync().GetAwaiter().GetResult();
        return 0;
    }
}
