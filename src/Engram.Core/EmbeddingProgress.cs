using System.Globalization;
using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>
/// What the backlog is doing, written by the server so anything else can read it.
/// </summary>
/// <remarks>
/// <para><b>Why a file rather than a query.</b> The backlog runs inside the server process, and
/// <c>embed --status</c> is a different process. Counts are not the problem — how many facts are
/// embedded and how many are waiting is a query any reader can run, and the database is the
/// authority on both. What no reader can derive is whether the loop is <i>alive</i>, how fast it is
/// going, what it is working on, and why it stopped. Those live only in the running loop, so the
/// loop has to write them down. <see cref="MetalRecord"/> is the same shape for the same reason:
/// the process that knows records, the process that asks reads.</para>
///
/// <para><b>Counts are deliberately not in here.</b> Duplicating them would create a second answer
/// to a question the store already answers, and the copy goes stale the moment the server stops —
/// the exact state in which someone is most likely to be reading this file. What is here is what
/// the store cannot say.</para>
///
/// <para><b>A stale timestamp is the signal.</b> The loop waits two seconds between passes when
/// busy and thirty when idle, so a file older than that with work outstanding means the backlog is
/// stuck rather than slow — which is the failure that was previously invisible, because the number
/// not moving looks identical whether the loop is grinding, wedged, or dead.</para>
/// </remarks>
public sealed record EmbeddingProgress(
    DateTimeOffset UpdatedAt,
    DateTimeOffset StartedAt,
    int Pid,
    string? Space,
    int SessionEmbedded,
    int SessionFailed,
    string? Outcome,
    string? LastError,
    IReadOnlyList<string> Recent)
{
    /// <summary>How many recently embedded facts are kept. Enough to show motion, not a log.</summary>
    public const int RecentKept = 8;

    /// <summary>The outcome of a loop that never started. <c>LastError</c> carries why.</summary>
    /// <remarks>
    /// A standing statement rather than a heartbeat: it is written once, by a service that is about
    /// to return, and it stays true until something restarts. So <see cref="LooksLive"/> excludes it
    /// outright rather than letting it age into "stalled", which would replace a precise reason with
    /// a vague one after forty-five seconds.
    /// </remarks>
    public const string Unavailable = "unavailable";

    /// <summary>How much of a fact body is kept. The file is rewritten every pass, so it stays small.</summary>
    public const int RecentLength = 160;

    /// <summary>Mean facts per second since this backlog run started, or null before there is one.</summary>
    /// <remarks>
    /// Mean since the run began rather than an instantaneous rate: a one-shot reader cannot sample
    /// twice without waiting, and waiting is what <c>--status</c> exists to avoid. It is stated as a
    /// mean where it is printed, because a backlog that stalled for ten minutes and then resumed
    /// would otherwise report an encouraging number.
    /// </remarks>
    public double? RatePerSecond
    {
        get
        {
            var elapsed = (UpdatedAt - StartedAt).TotalSeconds;
            return elapsed > 0 && SessionEmbedded > 0 ? SessionEmbedded / elapsed : null;
        }
    }

    /// <summary>Whether the loop has reported in recently enough to still be believed.</summary>
    /// <remarks>
    /// Keyed to the idle interval with room to spare, because that is the longest a healthy loop
    /// stays quiet. The timestamp is the whole test and the pid is not consulted: a note left behind
    /// by a server that was killed goes stale on its own, whereas a pid check would have to
    /// distinguish a recycled one — the problem D42 needed a start token to solve — to answer a
    /// question the clock already answers.
    /// </remarks>
    public bool LooksLive(DateTimeOffset now) =>
        Outcome != Unavailable
        && now - UpdatedAt < EmbeddingBacklog.IdleInterval + TimeSpan.FromSeconds(15);

    /// <summary>Records that there is no loop this time, and the reason there is not.</summary>
    public static void WriteUnavailable(EngramHome home, string reason) =>
        Write(
            home,
            new EmbeddingProgress(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                Space: null,
                SessionEmbedded: 0,
                SessionFailed: 0,
                Unavailable,
                reason,
                []));

    public static void Write(EngramHome home, EmbeddingProgress progress)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            var recent = new JsonArray();
            foreach (var line in progress.Recent)
            {
                // JsonArray.Add binds to the AOT-hostile generic overload without this cast.
                ((IList<JsonNode?>)recent).Add(JsonValue.Create(line));
            }

            var body = new JsonObject
            {
                ["updated_at"] = JsonValue.Create(Stamp(progress.UpdatedAt)),
                ["started_at"] = JsonValue.Create(Stamp(progress.StartedAt)),
                ["pid"] = JsonValue.Create(progress.Pid),
                ["space"] = JsonValue.Create(progress.Space),
                ["session_embedded"] = JsonValue.Create(progress.SessionEmbedded),
                ["session_failed"] = JsonValue.Create(progress.SessionFailed),
                ["outcome"] = JsonValue.Create(progress.Outcome),
                ["last_error"] = JsonValue.Create(progress.LastError),
                ["recent"] = recent,
            };

            var temporary = home.EmbeddingProgressPath + ".tmp";
            File.WriteAllText(temporary, body.ToJsonString());
            File.Move(temporary, home.EmbeddingProgressPath, overwrite: true);
        }
#pragma warning disable CA1031 // A progress note that cannot be written must not stop the embedding.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The last note, or null when there is none or it cannot be read.</summary>
    /// <remarks>
    /// Malformed is absent, not broken, for the same reason as <see cref="MetalRecord"/>: the next
    /// pass rewrites this two seconds from now, so a corrupt file heals itself and reporting it as a
    /// fault would ask for a fix that is already happening.
    /// </remarks>
    public static EmbeddingProgress? Read(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);

        try
        {
            if (!File.Exists(home.EmbeddingProgressPath))
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(home.EmbeddingProgressPath)) is not JsonObject body)
            {
                return null;
            }

            if (Parse(body["updated_at"]?.GetValue<string>()) is not { } updated)
            {
                // Without a timestamp nothing here can be trusted — every question this file
                // answers is "as of when".
                return null;
            }

            var recent = new List<string>();
            if (body["recent"] is JsonArray lines)
            {
                foreach (var line in lines)
                {
                    if (line?.GetValue<string>() is { } text)
                    {
                        recent.Add(text);
                    }
                }
            }

            return new EmbeddingProgress(
                updated,
                Parse(body["started_at"]?.GetValue<string>()) ?? updated,
                body["pid"]?.GetValue<int>() ?? 0,
                body["space"]?.GetValue<string>(),
                body["session_embedded"]?.GetValue<int>() ?? 0,
                body["session_failed"]?.GetValue<int>() ?? 0,
                body["outcome"]?.GetValue<string>(),
                body["last_error"]?.GetValue<string>(),
                recent);
        }
#pragma warning disable CA1031 // Anything unreadable here is a file the next pass replaces.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Removes the note, so a stopped server does not leave one claiming to be current.</summary>
    public static void Clear(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);

        try
        {
            File.Delete(home.EmbeddingProgressPath);
        }
#pragma warning disable CA1031 // Failing to tidy up is not worth failing a shutdown over.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>One fact body, flattened to a single line and cut to <see cref="RecentLength"/>.</summary>
    /// <remarks>
    /// Newlines are replaced rather than kept: this is read back into a fixed-height display, and a
    /// body containing a newline would cost a row the caller did not count (D52).
    /// </remarks>
    public static string Summarize(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var flat = body.ReplaceLineEndings(" ").Trim();
        return flat.Length <= RecentLength ? flat : string.Concat(flat.AsSpan(0, RecentLength - 1), "…");
    }

    private static string Stamp(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, out var moment) ? moment : null;
}
