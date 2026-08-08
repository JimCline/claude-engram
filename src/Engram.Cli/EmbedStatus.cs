using System.Globalization;
using Engram.Core;

namespace Engram.Cli;

/// <summary>Everything <c>embed --status</c> reports, gathered before anything is formatted.</summary>
/// <remarks>
/// A record rather than direct printing so the numbers can be asserted without parsing a screen,
/// and so the plain and live renderings cannot disagree about what they are showing.
/// </remarks>
public sealed record EmbedStatusView(
    string? Space,
    string Provider,
    int Embedded,
    int Pending,
    EmbeddingProgress? Progress,
    bool Live,
    string? Note)
{
    public int Total => Embedded + Pending;

    /// <summary>Fraction embedded, or null when there is nothing to embed at all.</summary>
    /// <remarks>
    /// Null rather than 100% for an empty store: a bar reading "complete" for a store with no facts
    /// answers a question nobody asked and looks like success.
    /// </remarks>
    public double? Fraction => Total > 0 ? (double)Embedded / Total : null;

    /// <summary>
    /// How long the remainder will take at the observed mean, or null when that cannot be said.
    /// </summary>
    /// <remarks>
    /// Null when the backlog is not running, because a rate measured by a process that has since
    /// stopped predicts nothing. An estimate is worse than no estimate when it is confidently wrong.
    /// </remarks>
    public TimeSpan? Eta =>
        Live && Pending > 0 && Progress?.RatePerSecond is { } rate && rate > 0
            ? TimeSpan.FromSeconds(Pending / rate)
            : null;
}

/// <summary>
/// <c>engram embed --status</c> — how far the vector index has got, and whether it is still moving.
/// </summary>
/// <remarks>
/// <para><b>Counts come from the store; liveness comes from the file.</b> The database is the
/// authority on how many facts are embedded and how many are waiting, and it answers whether or not
/// a server is up. What it cannot say is whether anything is currently working on the remainder —
/// that lives in the server process, which writes <see cref="EmbeddingProgress"/> so this can read
/// it. Splitting it that way means a stopped server gives correct counts and an honest "nothing is
/// running", rather than a cached number and a rate that has not been true for an hour.</para>
///
/// <para><b>Plain by default.</b> This is the command run for an answer, and the one a script or an
/// agent parses, so the redirected output is stable key-and-value lines. <c>--watch</c> is the live
/// version and the only thing that redraws.</para>
/// </remarks>
public static class EmbedStatus
{
    /// <summary>How often <c>--watch</c> re-reads. Matches the backlog's busy interval.</summary>
    /// <remarks>
    /// Polling faster than the thing being watched publishes only redraws the same numbers. The
    /// backlog writes on every committed batch, so this is as fresh as the data can be.
    /// </remarks>
    public static readonly TimeSpan WatchInterval = EmbeddingBacklog.BusyInterval;

    private const int BarWidth = 28;

    public static EmbedStatusView Read(EngramHome home, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(home);

        var settings = EmbeddingSettings.Read(ConfigFile.Load(home.ConfigPath));
        var provider = settings.Provider.ToString().ToLowerInvariant();
        var progress = EmbeddingProgress.Read(home);
        var live = progress?.LooksLive(now) == true;

        if (!File.Exists(home.DatabasePath))
        {
            return new EmbedStatusView(null, provider, 0, 0, progress, live, "no store yet — engram init");
        }

        using var connection = EngramDatabase.Open(home);

        // Embeddable rather than pending: pending is a SQL error against a store whose index was
        // dropped, and "how many facts could be embedded" is the denominator either way.
        var embedded = VectorIndex.Exists(connection) ? VectorIndex.Count(connection, liveOnly: true) : 0;
        var embeddable = VectorIndex.CountEmbeddable(connection);
        var space = VectorIndex.Exists(connection) ? VectorIndex.ReadSpace(connection)?.ToString() : null;

        var note = settings.Provider == EmbeddingProvider.None
            ? "embeddings are off — engram init --with-embeddings"
            : null;

        return new EmbedStatusView(
            space ?? settings.Model,
            provider,
            embedded,
            Math.Max(0, embeddable - embedded),
            progress,
            live,
            note);
    }

    /// <summary>The lines of the report, in order. Rendering is the caller's business.</summary>
    public static IReadOnlyList<string> Lines(EmbedStatusView view, DateTimeOffset now, bool decorated)
    {
        ArgumentNullException.ThrowIfNull(view);

        var lines = new List<string>
        {
            $"{view.Space ?? "no index"} ({view.Provider})",
            string.Empty,
        };

        if (decorated && view.Fraction is { } fraction)
        {
            var filled = (int)Math.Round(fraction * BarWidth, MidpointRounding.ToZero);
            lines.Add(
                "  [" + new string('█', filled) + new string('░', BarWidth - filled) + "]  "
                + $"{view.Embedded} / {view.Total} facts  {(int)(fraction * 100)}%");
        }
        else
        {
            lines.Add($"  embedded   {view.Embedded} of {view.Total} facts"
                + (view.Fraction is { } percent ? $" ({(int)(percent * 100)}%)" : string.Empty));
        }

        lines.Add($"  remaining  {view.Pending}");

        // Stated as a mean, and only while something is running. A rate left over from a server
        // that has stopped describes nothing that is happening now.
        lines.Add(view.Live && view.Progress?.RatePerSecond is { } rate
            ? $"  rate       {rate.ToString("0.0", CultureInfo.InvariantCulture)}/s mean since "
                + view.Progress.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "  rate       —");

        lines.Add($"  eta        {(view.Eta is { } eta ? "~" + Duration(eta) : "—")}");
        lines.Add($"  backlog    {Backlog(view, now)}");

        if (view.Note is { } note)
        {
            lines.Add($"  note       {note}");
        }

        if (view.Progress?.LastError is { Length: > 0 } error)
        {
            lines.Add($"  last error {error}");
        }

        if (view.Progress is { SessionFailed: > 0 } failures)
        {
            lines.Add($"  failed     {failures.SessionFailed} this run — they stay queued and are retried");
        }

        if (view.Live && view.Progress is { Recent.Count: > 0 } recent)
        {
            lines.Add(string.Empty);
            lines.Add("  recently embedded");
            foreach (var body in recent.Recent)
            {
                lines.Add("    " + body);
            }
        }

        return lines;
    }

    private static string Backlog(EmbedStatusView view, DateTimeOffset now)
    {
        if (view.Progress is not { } progress)
        {
            return view.Pending > 0
                ? "not running — start the server with `engram start`"
                : "not running";
        }

        // The server declined to start the loop and said why. Reported ahead of the staleness
        // branch and without regard to age: this was the case that sent a person to start a server
        // that was already up, because the only thing that knew the real reason wrote it to a log
        // nobody asking this question has cause to open.
        if (progress.Outcome == EmbeddingProgress.Unavailable)
        {
            return $"not running — {progress.LastError}";
        }

        var age = Duration(now - progress.UpdatedAt);

        // A note whose timestamp has gone stale is the signal this file exists for: a loop that is
        // merely slow still stamps it every pass, so one that has stopped stamping is stuck or gone.
        return view.Live
            ? $"running, pid {progress.Pid}, last update {age} ago"
            : $"stalled or stopped — pid {progress.Pid} last reported {age} ago";
    }

    private static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
            : $"{(int)span.TotalSeconds}s";
    }
}
