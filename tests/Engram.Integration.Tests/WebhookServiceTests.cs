using System.Collections.Concurrent;
using System.Net;
using Engram.Cli;
using Engram.Core;
using Microsoft.Extensions.Logging;

namespace Engram.Integration.Tests;

/// <summary>
/// Tier 2. Delivery is only meaningful against something that actually accepts a request, so these
/// run a real listener on loopback and assert on what arrived at it.
/// </summary>
public class WebhookServiceTests
{
    /// <summary>A port nothing is bound to, for the subscriber that is supposed to be dead.</summary>
    private const string Unreachable = "http://127.0.0.1:9/engram";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Ports are handed out once and never reused. A scan that restarts at the same number each
    /// time hands a fresh sink the port a finished one just released, and HttpListener on Unix does
    /// not take the port exclusively — so both binds succeed, the two listeners split the arriving
    /// requests, and whichever test is asserting sees a delivery that went to the other one. That
    /// presents as "nothing was delivered" in a full run and passes in isolation.
    /// </summary>
    private static int nextPort = 8900;

    private sealed class Sink : IDisposable
    {
        private readonly HttpListener listener = new();

        public ConcurrentQueue<(string Kind, string Body)> Received { get; } = new();

        public string Url { get; }

        public Sink()
        {
            string? bound = null;
            for (var attempt = 0; attempt < 80 && bound is null; attempt++)
            {
                var candidate = $"http://127.0.0.1:{Interlocked.Increment(ref nextPort)}/engram/";
                try
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add(candidate);
                    listener.Start();
                    bound = candidate;
                }
                catch (HttpListenerException)
                {
                    // Taken by something else on this machine; try the next one.
                }
            }

            Url = bound ?? throw new InvalidOperationException("no free loopback port for the sink");
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                using (var reader = new StreamReader(context.Request.InputStream))
                {
                    Received.Enqueue((
                        context.Request.Headers["X-Engram-Event"] ?? "",
                        await reader.ReadToEndAsync().ConfigureAwait(false)));
                }

                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            listener.Close();
        }
    }

    private static void Configure(SandboxHome sandbox, string body) =>
        File.WriteAllText(sandbox.Home.ConfigPath, body);

    private static void Record(SandboxHome sandbox, string kind, string? query = null) =>
        Telemetry.Append(sandbox.Home, new TelemetryRecord(
            DateTimeOffset.UtcNow.ToString("O"), "session-under-test", kind, query));


    private static async Task<bool> Settles(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return condition();
    }

    private sealed class Captured : ILogger<WebhookService>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
        }

        public override string ToString() => string.Join(" | ", Lines);
    }

    /// <param name="expectsToDeliver">
    /// Whether this configuration produces a running tail. A service with no subscriber returns
    /// before it logs anything, so there is no barrier to wait on and none is needed — nothing can
    /// race a tail that does not exist.
    /// </param>
    private static async Task WithService(
        SandboxHome sandbox, Func<Task> body, bool expectsToDeliver = true)
    {
        var log = new Captured();
        var service = new WebhookService(sandbox.Home, log);
        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

        // StartAsync promises only that ExecuteAsync was handed to the scheduler, not that it has
        // run — so without this barrier the test's own events can be written before the tail takes
        // its starting mark, and a service that is working perfectly reports nothing delivered.
        // That failed under load and passed in isolation, which is exactly what it looks like. The
        // startup line is logged after the mark is taken, so seeing it is the guarantee.
        if (expectsToDeliver)
        {
            Assert.True(
                await Settles(() =>
                    log.Lines.Any(line => line.Contains("Webhook delivering", StringComparison.Ordinal))),
                "the webhook service never reported its configuration");
        }

        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            service.Dispose();
        }
    }

    [Fact]
    public async Task AnEventRecordedWhileTheServiceRuns_IsDelivered()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var sink = new Sink();
        Configure(sandbox, $"[webhook]\nurl = \"{sink.Url}\"\n");

        await WithService(sandbox, async () =>
        {
            Record(sandbox, TelemetryEventKind.Remember, "a thing worth keeping");

            Assert.True(await Settles(() => !sink.Received.IsEmpty), "nothing was delivered");
            Assert.True(sink.Received.TryDequeue(out var delivered));
            Assert.Equal(TelemetryEventKind.Remember, delivered.Kind);

            // The body is the log line verbatim, so it parses through the same reader that reads
            // history out of the file.
            var record = Telemetry.TryParse(delivered.Body);
            Assert.NotNull(record);
            Assert.Equal(TelemetryEventKind.Remember, record.Kind);
            Assert.Equal("a thing worth keeping", record.Query);
        });
    }

    /// <summary>
    /// The no-resume contract. A tail that started at zero would replay the whole log at whatever
    /// is listening every time the server restarts, which for <c>file-touched</c> is thousands of
    /// requests describing edits from days ago.
    /// </summary>
    [Fact]
    public async Task EventsRecordedBeforeTheServiceStarted_AreNotDelivered()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var sink = new Sink();
        Configure(sandbox, $"[webhook]\nurl = \"{sink.Url}\"\n");

        Record(sandbox, TelemetryEventKind.Recall, "history");

        await WithService(sandbox, async () =>
        {
            Record(sandbox, TelemetryEventKind.Remember, "live");

            Assert.True(await Settles(() => !sink.Received.IsEmpty), "nothing was delivered");

            // Give the poll another turn, so a late delivery of the earlier record would be seen
            // rather than raced past.
            await Task.Delay(WebhookService.PollInterval * 3).ConfigureAwait(false);

            var bodies = sink.Received.Select(entry => entry.Body).ToList();
            Assert.Single(bodies);
            Assert.Contains("live", bodies[0], StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AKindsFilter_DropsWhatWasNotAskedFor()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var sink = new Sink();
        Configure(sandbox, $"[webhook]\nurl = \"{sink.Url}\"\nkinds = [\"remember\"]\n");

        await WithService(sandbox, async () =>
        {
            Record(sandbox, TelemetryEventKind.FileTouched);
            Record(sandbox, TelemetryEventKind.Remember, "kept");

            Assert.True(await Settles(() => !sink.Received.IsEmpty), "nothing was delivered");
            await Task.Delay(WebhookService.PollInterval * 3).ConfigureAwait(false);

            var kinds = sink.Received.Select(entry => entry.Kind).ToList();
            Assert.Equal([TelemetryEventKind.Remember], kinds);
        });
    }

    /// <summary>
    /// One subscriber being down must not cost the other one its events. Delivery is per URL and a
    /// failure mutes only the URL that failed — the alternative is a status line that goes dark
    /// because an unrelated dashboard was closed.
    /// </summary>
    [Fact]
    public async Task ADeadSubscriber_DoesNotStopDeliveryToALiveOne()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var sink = new Sink();
        Configure(
            sandbox,
            $"[webhook]\nurls = [\"{Unreachable}\", \"{sink.Url}\"]\ntimeout_ms = 500\n");

        await WithService(sandbox, async () =>
        {
            Record(sandbox, TelemetryEventKind.Remember, "first");
            Record(sandbox, TelemetryEventKind.Remember, "second");

            Assert.True(
                await Settles(() => sink.Received.Count >= 2),
                $"the live subscriber got {sink.Received.Count} of 2");
        });
    }

    [Fact]
    public async Task WithNoSubscriberConfigured_TheServiceStartsAndStopsWithoutDelivering()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var sink = new Sink();
        Configure(sandbox, "[webhook]\nkinds = [\"*\"]\n");

        await WithService(sandbox, async () =>
        {
            Record(sandbox, TelemetryEventKind.Remember, "unheard");
            await Task.Delay(WebhookService.PollInterval * 3).ConfigureAwait(false);

            Assert.Empty(sink.Received);
        }, expectsToDeliver: false);
    }

    [Fact]
    public async Task AMalformedUrl_DeliversNothingRatherThanCrashingTheServer()
    {
        using var sandbox = new SandboxHome(initialize: false);
        using var sink = new Sink();
        Configure(sandbox, "[webhook]\nurl = \"not-a-url\"\n");

        await WithService(sandbox, async () =>
        {
            Record(sandbox, TelemetryEventKind.Remember, "unheard");
            await Task.Delay(WebhookService.PollInterval * 3).ConfigureAwait(false);

            Assert.Empty(sink.Received);
        }, expectsToDeliver: false);
    }
}
