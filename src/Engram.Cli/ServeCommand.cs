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

        // Read once, here, rather than wherever a tool decision or a telemetry record needs it —
        // this is the value every registration and stamp in this method must agree with for the
        // life of the connection, even if config.toml changes mid-run (Hazard 6).
        var toolProfile = ToolProfileSettings.Read(ConfigFile.Load(home.ConfigPath)).Profile;

        // Built once, before the server starts accepting requests, so a store that predates
        // fact_token — or one whose tokenizer version fell behind this build — is never served
        // against a stale or missing index. Cheap in the ordinary case: EnsureBuilt reads one
        // schema_meta row and returns when the store is already current.
        using (var connection = EngramDatabase.OpenInitialized(home))
        {
            FactTokenIndex.EnsureBuilt(connection);
        }

        var identity = new ServerIdentity(
            Environment.ProcessId,
            resolvedPort,
            EngramVersion.Current,
            Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            ProcessStartToken.ForSelf());
        var openedSessions = new ConcurrentDictionary<string, byte>();

        var builder = WebApplication.CreateSlimBuilder([]);

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(home.LogPath));

        // A single session's startup wrote 27 lines at the framework default, six of them
        // per MCP call, into a file nothing truncates — a long-lived daemon would grow it
        // without bound for no diagnostic value. Warnings and errors are what anyone reads
        // this file for. Hosting.Lifetime stays at Information because "Now listening on"
        // is the one line that tells you a failed start got as far as binding.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        // The backlog is the second exception, and it is one for the same reason: it is the only
        // work this process does that a person waits on. It had been writing "Embedded N fact(s)"
        // since it was built and every line was dropped here, so a 15-minute backfill left a log
        // saying nothing about the only thing happening. It is quiet by construction rather than by
        // filtering — a line per pass that embedded something, and nothing at all while idle — so
        // the bound that justifies Warning everywhere else does not apply to it.
        builder.Logging.AddFilter(typeof(EmbeddingBacklogService).FullName, LogLevel.Information);

        // The webhook states once, at startup, where it is delivering and what it is filtered to.
        // That line is the only place a subscriber that never fires can be told apart from one
        // that was never configured, and it costs one line per server rather than one per event.
        builder.Logging.AddFilter(typeof(WebhookService).FullName, LogLevel.Information);

        // Same exception, same reason: quiet by construction (idle ticks publish nothing but the
        // note file), so the Warning-everywhere-else bound does not starve it of its one line per
        // repo actually freshened.
        builder.Logging.AddFilter(typeof(IndexFreshnessService).FullName, LogLevel.Information);

        builder.WebHost.UseUrls($"http://127.0.0.1:{resolvedPort}");

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(home);
        builder.Services.AddSingleton(identity);
        builder.Services.AddTransient(_ => new McpHomeState(File.Exists(home.ConfigPath)));
        builder.Services.AddTransient(services => ResolveSessionId(services, home, openedSessions, toolProfile));

        // Registered even when no model will ever be asked for, because constructing it starts
        // nothing — the first Open does. Held by the container so the child process it may spawn
        // is disposed on shutdown by the same machinery that stops everything else, and so the
        // backlog and the query side share one server instead of loading the model twice.
        builder.Services.AddSingleton(_ => new LocalRuntime(home));

        // Per-session pin (docs/memory-expansion/04-lifecycle-spec.md) — one store for the life of
        // the server, keyed internally by McpSessionId, never persisted (D8).
        builder.Services.AddSingleton<SessionPinStore>();

        // The singular embedding service. One server per home, so one embedder — no lock, no
        // second daemon. It self-disables when no provider is configured, which is the ordinary
        // case, so registering it unconditionally costs a started-and-returned task.
        builder.Services.AddHostedService<EmbeddingBacklogService>();

        // The background repo-freshness loop (spec §6). Registered unconditionally for the same
        // reason as the backlog — with auto_index_in_background left at its default off, it writes
        // one Unavailable note and returns, which is the ordinary case.
        builder.Services.AddHostedService<IndexFreshnessService>();

        // Delivery of the telemetry log to whoever subscribed. Registered unconditionally for the
        // same reason as the backlog — with no URL configured it returns immediately, which is the
        // ordinary case — and it is the only component permitted to make outbound HTTP, because
        // every other producer of these events is a hook on a latency budget.
        builder.Services.AddHostedService<WebhookService>();

        // The generic WithTools<T>() calls below are load-bearing, not a style choice: the SDK's
        // own non-generic WithTools(IEnumerable<Type>, ...) carries
        // [RequiresUnreferencedCode("... might not work in Native AOT. Use the generic WithTools
        // method instead.")], and does not — an AOT-published server registered zero tools
        // through it, discovered by ToolProfileEndToEndTests and McpServerTests failing against
        // the published binary while every JIT-run Tier 2 test still passed. So the type
        // arguments stay compile-time literals; what stays single-sourced is which of them run,
        // read from ToolTypesFor rather than re-deriving the profile switch here.
        var registered = ToolTypesFor(toolProfile);
        var mcpBuilder = builder.Services.AddMcpServer()
            // The SDK defaults HttpServerTransportOptions.Stateless to true as of the
            // 2026-07-28 protocol revision (SEP-2567), which mints no Mcp-Session-Id at
            // all. D14's session-keyed working memory and the session-open adoption
            // metric both depend on that header existing and being stable for a
            // session's lifetime, so this must stay false. Do not "simplify" this to
            // the default — that silently drops session identity with no error anywhere.
            .WithHttpTransport(options => options.Stateless = false);

        if (registered.Contains(typeof(EngramMcpTools)))
        {
            mcpBuilder = mcpBuilder.WithTools<EngramMcpTools>();
        }

        if (registered.Contains(typeof(EngramServerTools)))
        {
            mcpBuilder = mcpBuilder.WithTools<EngramServerTools>();
        }

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
            Results.Json(identity.ToHealthPayload(), HealthResponseJsonContext.Default.HealthResponsePayload));

        // On ApplicationStarted rather than beside app.Run(), so the event means the server is
        // accepting requests rather than about to try and possibly fail on a bound port.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (File.Exists(home.ConfigPath))
            {
                Telemetry.Append(home, new TelemetryRecord(
                    Timestamp: DateTime.UtcNow.ToString("o"),
                    SessionId: "server",
                    Kind: TelemetryEventKind.ServerStart));
            }
        });

        // Leave no pid file behind claiming a process that has exited — but only ours.
        // An orphan being replaced must not delete the record its replacement just wrote.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (File.Exists(home.ConfigPath))
            {
                Telemetry.Append(home, new TelemetryRecord(
                    Timestamp: DateTime.UtcNow.ToString("o"),
                    SessionId: "server",
                    Kind: TelemetryEventKind.ServerStop));
            }

            if (PidFile.Read(home)?.Pid == identity.Pid)
            {
                PidFile.Delete(home);
            }

            // Same rule for the embedding note, and the same ownership test. It states what this
            // server decided about its backlog, including a decision not to run one — which the
            // loop's own cleanup cannot clear, because in that case the loop never started.
            if (EmbeddingProgress.Read(home)?.Pid == identity.Pid)
            {
                EmbeddingProgress.Clear(home);
            }

            // Same rule and same ownership test as the embedding note. A declined freshness
            // service never enters its loop and so never reaches the loop's own cleanup — this is
            // the only place that case is cleared.
            if (IndexProgress.Read(home)?.Pid == identity.Pid)
            {
                IndexProgress.Clear(home);
            }
        });

        app.MapMcp();

        app.Run();
        return 0;
    }

    private static McpSessionId ResolveSessionId(
        IServiceProvider services, EngramHome home, ConcurrentDictionary<string, byte> openedSessions, ToolProfile toolProfile)
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
            Telemetry.Append(home, BuildSessionOpenRecord(header, toolProfile));
        }

        return new McpSessionId(header);
    }

    /// <summary>The tool types this profile registers — <c>EngramMcpTools</c> always, and
    /// <c>EngramServerTools</c> only under <see cref="ToolProfile.Full"/> (D-5).</summary>
    internal static IReadOnlyList<Type> ToolTypesFor(ToolProfile profile) => profile switch
    {
        ToolProfile.Full => [typeof(EngramMcpTools), typeof(EngramServerTools)],
        _ => [typeof(EngramMcpTools)],
    };

    internal static TelemetryRecord BuildSessionOpenRecord(string sessionId, ToolProfile toolProfile) =>
        new(
            Timestamp: DateTime.UtcNow.ToString("o"),
            SessionId: sessionId,
            Kind: TelemetryEventKind.SessionOpen,
            ToolProfile: ToolProfileSettings.ToText(toolProfile));
}
