namespace Engram.Core;

/// <summary>
/// The <c>[webhook]</c> section: where to POST events as they are recorded.
/// </summary>
/// <remarks>
/// <para>There is no <c>enabled</c> key. A configured URL is the switch, because two ways to turn
/// one thing off is how a setting ends up disagreeing with itself — comment the URL out.</para>
/// <para>Misconfiguration is reported, never thrown, for the same reason the embedding section
/// works that way: a bad URL must leave Engram recording and serving memory, and able to say why
/// nothing is being delivered.</para>
/// </remarks>
/// <param name="Unknown">
/// Kinds named in <c>kinds</c> that Engram never emits, described. Deliberately not a problem: a
/// typo there should deliver less, not switch delivery off, and the failure it produces —
/// a filter that silently matches nothing — is exactly the kind only a diagnostic can surface.
/// </param>
public sealed record WebhookSettings(
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> Kinds,
    TimeSpan Timeout,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Unknown)
{
    public const string Section = "webhook";
    public const int DefaultTimeoutMilliseconds = 2000;

    /// <summary>The <c>kinds</c> wildcard, and the default.</summary>
    public const string EveryKind = "*";

    public static WebhookSettings Disabled { get; } = new(
        [], [EveryKind], TimeSpan.FromMilliseconds(DefaultTimeoutMilliseconds), [], []);

    /// <summary>True when somewhere to deliver is configured and nothing is wrong with how.</summary>
    public bool IsEnabled => Urls.Count > 0 && Problems.Count == 0;

    public bool Wants(string kind) =>
        Kinds.Contains(EveryKind, StringComparer.Ordinal)
        || Kinds.Contains(kind, StringComparer.Ordinal);

    public static WebhookSettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();
        var urls = new List<string>();

        foreach (var candidate in Candidates(config))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                problems.Add($"[webhook] url \"{candidate}\" is not an http or https URL.");
            }
            else if (!urls.Contains(candidate, StringComparer.Ordinal))
            {
                urls.Add(candidate);
            }
        }

        var kinds = config.Strings(Section, "kinds");
        var unknown = kinds
            .Where(kind => kind != EveryKind && !TelemetryEventKind.All.Contains(kind, StringComparer.Ordinal))
            .Select(kind => $"kinds names \"{kind}\", which Engram never emits")
            .ToList();

        var timeout = config.Int(Section, "timeout_ms") ?? DefaultTimeoutMilliseconds;
        if (timeout <= 0)
        {
            problems.Add($"[webhook] timeout_ms must be positive; found {timeout}.");
            timeout = DefaultTimeoutMilliseconds;
        }

        return new WebhookSettings(
            urls,
            kinds.Count > 0 ? kinds : [EveryKind],
            TimeSpan.FromMilliseconds(timeout),
            problems,
            unknown);
    }

    /// <summary>
    /// Both spellings, in the order a reader would expect them to win.
    /// </summary>
    /// <remarks>
    /// One subscriber is the ordinary case and <c>url = "…"</c> is what that should look like;
    /// <c>urls = [ … ]</c> exists because a live dashboard and a status-line script are two
    /// consumers of one stream, which is the whole point of delivering it.
    /// </remarks>
    private static IEnumerable<string> Candidates(ConfigFile config)
    {
        if (config.String(Section, "url") is { } single)
        {
            yield return single;
        }

        foreach (var many in config.Strings(Section, "urls"))
        {
            yield return many;
        }
    }
}
