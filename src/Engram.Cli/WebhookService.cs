using System.Net.Http;
using System.Text;
using Engram.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Engram.Cli;

/// <summary>
/// Tails <c>telemetry.jsonl</c> and POSTs each new record to the configured subscribers.
/// </summary>
/// <remarks>
/// <para><b>This delivers the log; it is not a second event system.</b> Every kind Engram records
/// already lands in <c>telemetry.jsonl</c>, written by short-lived hooks and by this process
/// alike, so tailing the file is the only design in which a subscriber's live feed and a reader's
/// history cannot disagree. It tails its own writes too — there is no fast path for MCP events,
/// because a fast path is where the two views start to drift.</para>
///
/// <para><b>Only the server delivers.</b> The events come from hooks that must not do outbound
/// HTTP: <c>file-touched</c> holds a 10 ms budget and is forbidden from even opening the database
/// (D4), and a POST is far more expensive than the open it may not do. Writing a line and exiting
/// costs those hooks nothing, which is what makes the whole feed free at the point of emission.</para>
///
/// <para><b>There is no cursor and no resume.</b> The tail starts at the end of the file, so this
/// delivers what happens while the server runs and nothing else. That is a contract in one
/// sentence, and it is what stops a restart after a day of downtime from replaying thousands of
/// <c>file-touched</c> events at a status-line script. Nothing is lost by it: the log is durable
/// and addressed by timestamp, so history is a read of the file — which is what a dashboard
/// wanting more than the live tail should be doing anyway.</para>
///
/// <para><b>A failed delivery is dropped, never retried into a queue.</b> Delivery may not stall
/// the tail, because the alternative is memory latency held hostage by a subscriber nobody is
/// watching. Dropping is safe for exactly the reason above — the durable log recovers any event a
/// dashboard actually needs, and a status line does not care, since the next event supersedes a
/// lost one within seconds.</para>
/// </remarks>
internal sealed class WebhookService(
    EngramHome home,
    ILogger<WebhookService> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Records delivered per poll, so a burst drains steadily rather than in one blocking pass.
    /// </summary>
    internal const int MaxEventsPerPoll = 64;

    private static readonly TimeSpan FirstMute = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxMute = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = WebhookSettings.Read(ConfigFile.Load(home.ConfigPath));

        foreach (var problem in settings.Problems)
        {
            logger.LogWarning("{Problem}", problem);
        }

        if (!settings.IsEnabled)
        {
            return;
        }

        var path = Telemetry.ResolvePath(home);
        var tail = new TelemetryTail(path, TelemetryTail.EndOf(path));
        var subscribers = settings.Urls.Select(url => new Subscriber(url)).ToList();

        // One client for the life of the server. The timeout is the whole request, which is the
        // bound that matters here — a subscriber that accepts the connection and then hangs is
        // the failure this has to survive, not a refused one.
        using var client = new HttpClient { Timeout = settings.Timeout };

        logger.LogInformation(
            "Webhook delivering {Kinds} to {Count} subscriber(s): {Urls}",
            settings.Kinds.Contains(WebhookSettings.EveryKind) ? "every event" : string.Join(", ", settings.Kinds),
            subscribers.Count,
            string.Join(", ", settings.Urls));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await DrainAsync(tail, subscribers, client, settings, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // A poll that throws must cost that poll. Letting it out of ExecuteAsync ends the
                // BackgroundService for the life of the server, with no error anywhere the person
                // watching a status line would look — one unreadable record would silently stop
                // every event after it.
                logger.LogWarning(failure, "Webhook poll failed; continuing.");
            }
        }
    }

    private async Task DrainAsync(
        TelemetryTail tail,
        IReadOnlyList<Subscriber> subscribers,
        HttpClient client,
        WebhookSettings settings,
        CancellationToken stoppingToken)
    {
        // Every subscriber gets at most one failing attempt per poll. Without that bound a
        // subscriber that hangs rather than refuses costs the full timeout on every record in the
        // batch — 64 of them against a 2 s timeout is a two-minute poll, and the tail stops for
        // all of it.
        var spent = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in tail.Read(MaxEventsPerPoll))
        {
            if (Telemetry.TryParse(line) is not { } record || !settings.Wants(record.Kind))
            {
                continue;
            }

            foreach (var subscriber in subscribers)
            {
                var now = DateTimeOffset.UtcNow;
                if (subscriber.Muted(now) || spent.Contains(subscriber.Url))
                {
                    subscriber.Dropped++;
                    continue;
                }

                if (await DeliverAsync(client, subscriber.Url, line, record.Kind, stoppingToken)
                        .ConfigureAwait(false))
                {
                    subscriber.Succeeded();
                    continue;
                }

                spent.Add(subscriber.Url);
                var mute = subscriber.Failed(DateTimeOffset.UtcNow);

                logger.LogWarning(
                    "Webhook delivery to {Url} failed; muted for {Seconds:0.#}s, {Dropped} event(s) dropped so far.",
                    subscriber.Url,
                    mute.TotalSeconds,
                    subscriber.Dropped);
            }
        }
    }

    private async Task<bool> DeliverAsync(
        HttpClient client,
        string url,
        string line,
        string kind,
        CancellationToken stoppingToken)
    {
        try
        {
            // The body is the telemetry line exactly as written. An envelope would add a nesting
            // level every subscriber has to unwrap for no information, and would mean the live
            // feed and a read of the file parse differently — the one property this feed is for.
            using var content = new StringContent(line, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            // So a shell script can route on the kind without a JSON parser.
            request.Headers.TryAddWithoutValidation("X-Engram-Event", kind);
            request.Headers.TryAddWithoutValidation("X-Engram-Version", EngramVersion.Current);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            // The client's own timeout surfaces here as well as a real cancellation. Both mean
            // this record is not getting through, and neither is worth a stack trace in the log.
            return false;
        }
    }

    private sealed class Subscriber(string url)
    {
        private TimeSpan mute = FirstMute;

        public string Url { get; } = url;

        public int Dropped { get; set; }

        private DateTimeOffset MutedUntil { get; set; }

        public bool Muted(DateTimeOffset now) => now < MutedUntil;

        public void Succeeded()
        {
            mute = FirstMute;
            MutedUntil = default;
        }

        public TimeSpan Failed(DateTimeOffset now)
        {
            var applied = mute;
            MutedUntil = now + applied;
            mute = mute >= MaxMute ? MaxMute : Min(MaxMute, mute * 2);
            Dropped++;
            return applied;
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    }
}
