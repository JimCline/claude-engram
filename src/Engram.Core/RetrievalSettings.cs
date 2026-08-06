namespace Engram.Core;

/// <summary>
/// The <c>[retrieval]</c> section: how much recall may spend, and how deep each lane is drawn.
/// </summary>
/// <remarks>
/// <para>Both settings shipped in the default config before anything read them, which is its own
/// small bug — a key a user can edit and Engram ignores is worse than a key that is not there,
/// because the user has no way to tell the difference from the outside.</para>
///
/// <para><c>graph_hops</c> and <c>recency_half_life_days</c> are still in that state deliberately:
/// they configure graph expansion and recency decay, neither of which is built. They are left in
/// the shipped config as documentation of the shape, and marked as such there.</para>
/// </remarks>
public sealed record RetrievalSettings(
    int BudgetTokens,
    int SeedK,
    IReadOnlyList<string> Problems)
{
    public const string Section = "retrieval";

    public const int DefaultBudgetTokens = 500;

    /// <summary>
    /// How many candidates each lane contributes before fusion.
    /// </summary>
    /// <remarks>
    /// Per lane, not in total. The point of drawing a fixed depth from each is that a lane which
    /// would otherwise be drowned by a better-scoring one still gets its top hits in front of the
    /// fusion — which is the whole reason reciprocal rank fusion works on lanes whose scores are
    /// not comparable.
    /// </remarks>
    public const int DefaultSeedK = 32;

    public static RetrievalSettings Default { get; } = new(DefaultBudgetTokens, DefaultSeedK, []);

    public static RetrievalSettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();

        return new RetrievalSettings(
            Positive(config, "default_budget_tokens", DefaultBudgetTokens, problems),
            Positive(config, "seed_k", DefaultSeedK, problems),
            problems);
    }

    private static int Positive(ConfigFile config, string key, int fallback, List<string> problems)
    {
        if (config.Int(Section, key) is not { } value)
        {
            return fallback;
        }

        if (value <= 0)
        {
            problems.Add($"[{Section}] {key} must be greater than zero; using {fallback}.");
            return fallback;
        }

        return value;
    }
}
