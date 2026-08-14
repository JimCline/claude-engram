using System.Globalization;
using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>
/// What the background freshness loop is doing, written by the server so anything else can read it.
/// </summary>
/// <remarks>
/// <para>The third instance of the <c>embedding.json</c>/<c>metal.json</c> pattern (D42, D54): the
/// database is the authority on how many repos are due, correctly whether or not a server is up, so
/// this note carries only what the database cannot say — whether the loop is alive, which repo it is
/// on, and why a tick stopped.</para>
///
/// <para><b>A declining service records why.</b> <c>auto_index_in_background = false</c> writes an
/// explicit <see cref="Unavailable"/> note naming the setting rather than nothing, so a reader is
/// told the loop chose not to run rather than left to wonder whether it crashed before its first
/// tick.</para>
/// </remarks>
public sealed record IndexProgress(
    DateTimeOffset UpdatedAt,
    DateTimeOffset StartedAt,
    int Pid,
    string? StartToken,
    string? Repo,
    string? Outcome,
    string? LastError)
{
    /// <summary>The outcome of a loop that never started. <c>LastError</c> carries why.</summary>
    /// <remarks>
    /// A standing statement rather than a heartbeat, same as <see cref="EmbeddingProgress.Unavailable"/>:
    /// written once by a service about to return, so <see cref="LooksLive"/> excludes it outright
    /// rather than letting it age into "stalled".
    /// </remarks>
    public const string Unavailable = "unavailable";

    /// <summary>Whether the loop has ticked recently enough to still be believed.</summary>
    /// <remarks>
    /// Keyed to <see cref="IndexFreshness.PollInterval"/> with room to spare, the longest a healthy
    /// loop stays quiet between ticks.
    /// </remarks>
    public bool LooksLive(DateTimeOffset now) =>
        Outcome != Unavailable && now - UpdatedAt < IndexFreshness.PollInterval + TimeSpan.FromSeconds(30);

    /// <summary>Records that there is no loop this time, and the reason there is not.</summary>
    public static void WriteUnavailable(EngramHome home, string reason) =>
        Write(
            home,
            new IndexProgress(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                ProcessStartToken.ForSelf(),
                Repo: null,
                Unavailable,
                reason));

    public static void Write(EngramHome home, IndexProgress progress)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            var body = new JsonObject
            {
                ["updated_at"] = JsonValue.Create(Stamp(progress.UpdatedAt)),
                ["started_at"] = JsonValue.Create(Stamp(progress.StartedAt)),
                ["pid"] = JsonValue.Create(progress.Pid),
                ["start_token"] = JsonValue.Create(progress.StartToken),
                ["repo"] = JsonValue.Create(progress.Repo),
                ["outcome"] = JsonValue.Create(progress.Outcome),
                ["last_error"] = JsonValue.Create(progress.LastError),
            };

            var temporary = home.IndexProgressPath + ".tmp";
            File.WriteAllText(temporary, body.ToJsonString());
            File.Move(temporary, home.IndexProgressPath, overwrite: true);
        }
#pragma warning disable CA1031 // A progress note that cannot be written must not stop the freshness loop.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The last note, or null when there is none or it cannot be read.</summary>
    /// <remarks>
    /// Malformed is absent, not broken: the next tick rewrites this within
    /// <see cref="IndexFreshness.PollInterval"/>, so a corrupt file heals itself.
    /// </remarks>
    public static IndexProgress? Read(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);

        try
        {
            if (!File.Exists(home.IndexProgressPath))
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(home.IndexProgressPath)) is not JsonObject body)
            {
                return null;
            }

            if (Parse(body["updated_at"]?.GetValue<string>()) is not { } updated)
            {
                // Without a timestamp nothing here can be trusted — every question this file
                // answers is "as of when".
                return null;
            }

            return new IndexProgress(
                updated,
                Parse(body["started_at"]?.GetValue<string>()) ?? updated,
                body["pid"]?.GetValue<int>() ?? 0,
                body["start_token"]?.GetValue<string>(),
                body["repo"]?.GetValue<string>(),
                body["outcome"]?.GetValue<string>(),
                body["last_error"]?.GetValue<string>());
        }
#pragma warning disable CA1031 // Anything unreadable here is a file the next tick replaces.
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
            File.Delete(home.IndexProgressPath);
        }
#pragma warning disable CA1031 // Failing to tidy up is not worth failing a shutdown over.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static string Stamp(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, out var moment) ? moment : null;
}
