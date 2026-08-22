namespace Engram.Core;

/// <summary>
/// The <c>[remember]</c> section (docs/memory-expansion/02-conflict-verdicts-spec.md,
/// Measurements): whether <c>engram_remember</c> searches for near-neighbour candidates
/// after a fresh write.
/// </summary>
/// <remarks>
/// Opt-in, like <see cref="SyncSettings"/>: candidates run recall's three lanes
/// synchronously inside every fresh <c>engram_remember</c> call, and there is no cheap
/// same-slot fallback left to lean on instead — ships off until the added latency is
/// measured at scale.
/// </remarks>
public sealed record RememberSettings(bool Candidates)
{
    public const string Section = "remember";

    public const bool DefaultCandidates = false;

    public static RememberSettings Default { get; } = new(DefaultCandidates);

    public static RememberSettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new RememberSettings(config.Bool(Section, "candidates") ?? DefaultCandidates);
    }
}
