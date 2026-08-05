using System.Collections.Concurrent;
using System.Diagnostics;
using Engram.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Engram.Cli;

internal static class ServeCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        int? port = null;

        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] != "--port")
            {
                CliApp.PrintUsage(stderr);
                return 1;
            }

            if (i + 1 >= rest.Length)
            {
                stderr.WriteLine("error: --port requires a value");
                return 1;
            }

            if (!int.TryParse(rest[++i], out var parsed) || parsed is <= 0 or > 65535)
            {
                stderr.WriteLine("error: --port must be a valid TCP port number");
                return 1;
            }

            port = parsed;
        }

        var resolvedPort = ServerPort.Resolve(port);
        var home = EngramHome.ResolveFromProcess(homePath);
        var pid = Environment.ProcessId;
        var startTime = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var openedSessions = new ConcurrentDictionary<string, byte>();

        var builder = WebApplication.CreateSlimBuilder([]);

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(home.LogPath));

        builder.WebHost.UseUrls($"http://127.0.0.1:{resolvedPort}");

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(home);
        builder.Services.AddTransient(_ => new McpHomeState(File.Exists(home.ConfigPath)));
        builder.Services.AddTransient(services => ResolveSessionId(services, home, openedSessions));

        builder.Services.AddMcpServer()
            // The SDK defaults HttpServerTransportOptions.Stateless to true as of the
            // 2026-07-28 protocol revision (SEP-2567), which mints no Mcp-Session-Id at
            // all. D14's session-keyed working memory and the session-open adoption
            // metric both depend on that header existing and being stable for a
            // session's lifetime, so this must stay false. Do not "simplify" this to
            // the default — that silently drops session identity with no error anywhere.
            .WithHttpTransport(options => options.Stateless = false)
            .WithTools<EngramMcpTools>();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

        app.MapGet("/health", () =>
        {
            var payload = new HealthResponsePayload(pid, resolvedPort, EngramVersion.Current, startTime);
            return Results.Json(payload, HealthResponseJsonContext.Default.HealthResponsePayload);
        });

        app.MapMcp();

        app.Run();
        return 0;
    }

    private static McpSessionId ResolveSessionId(IServiceProvider services, EngramHome home, ConcurrentDictionary<string, byte> openedSessions)
    {
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var header = accessor.HttpContext?.Request.Headers["Mcp-Session-Id"].ToString();
        if (string.IsNullOrEmpty(header))
        {
            throw new InvalidOperationException("No Mcp-Session-Id header present on the current request.");
        }

        // D14 replaces the one-server-start-per-process telemetry record (only ever
        // valid under one-process-per-session stdio) with one session-open record per
        // real Mcp-Session-Id, since a daemon mints many sessions over its lifetime.
        if (File.Exists(home.ConfigPath) && openedSessions.TryAdd(header, 0))
        {
            Telemetry.Append(home, new TelemetryRecord(
                Timestamp: DateTime.UtcNow.ToString("o"),
                SessionId: header,
                Kind: TelemetryEventKind.SessionOpen));
        }

        return new McpSessionId(header);
    }
}
