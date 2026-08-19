namespace Engram.Core;

/// <summary>
/// The <c>[sync]</c> section (docs/memory-expansion/01-sync-spec.md): whether cross-machine sync
/// is on, where the chunk directory lives, the retry ceiling before a deferred close gives up,
/// and the export scope baseline.
/// </summary>
/// <remarks>
/// Opt-in, unlike <see cref="BackupSettings"/> — sync requires a git repo the user set up
/// themselves at <c>dir</c> (or the default <c>&lt;home&gt;/sync</c>), which nothing here creates
/// or manages (spec: "Engram does not shell out to git").
/// </remarks>
public sealed record SyncSettings(
    bool Enabled,
    string? Dir,
    int RetryCeiling,
    string Scope,
    int StaleAfterDays,
    int RetainDays,
    IReadOnlyList<string> Problems)
{
    public const string Section = "sync";

    public const bool DefaultEnabled = false;

    /// <summary>
    /// Default age past which a peer with no observed activity reads as stale
    /// (docs/memory-expansion/01-sync-spec.md, "Staleness/liveness detection").
    /// </summary>
    public const int DefaultStaleAfterDays = 14;

    /// <summary>
    /// Default age past which a closed fact is dropped from this machine's own consolidated chunk
    /// by <c>sync compact</c> (docs/memory-expansion/01-sync-spec.md, "Chunk retention/pruning").
    /// </summary>
    public const int DefaultRetainDays = 90;

    public static SyncSettings Default { get; } = new(
        DefaultEnabled,
        null,
        CloseResolver.DefaultRetryCeiling,
        SyncScope.Default,
        DefaultStaleAfterDays,
        DefaultRetainDays,
        []);

    public static SyncSettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();
        var retryCeiling = config.Int(Section, "retry_ceiling");
        var ceiling = CloseResolver.DefaultRetryCeiling;
        if (retryCeiling is not null)
        {
            if (retryCeiling < 1)
            {
                problems.Add($"[{Section}] retry_ceiling must be at least 1; using {ceiling}.");
            }
            else
            {
                ceiling = retryCeiling.Value;
            }
        }

        var scope = config.String(Section, "scope") ?? SyncScope.Default;
        if (!SyncScope.TryParse(scope, out _, out _, out var scopeError))
        {
            problems.Add($"[{Section}] {scopeError} Using '{SyncScope.Default}'.");
            scope = SyncScope.Default;
        }

        var staleAfterDaysRaw = config.Int(Section, "stale_after_days");
        var staleAfterDays = DefaultStaleAfterDays;
        if (staleAfterDaysRaw is not null)
        {
            if (staleAfterDaysRaw < 1)
            {
                problems.Add($"[{Section}] stale_after_days must be at least 1; using {staleAfterDays}.");
            }
            else
            {
                staleAfterDays = staleAfterDaysRaw.Value;
            }
        }

        var retainDaysRaw = config.Int(Section, "retain_days");
        var retainDays = DefaultRetainDays;
        if (retainDaysRaw is not null)
        {
            if (retainDaysRaw < 1)
            {
                problems.Add($"[{Section}] retain_days must be at least 1; using {retainDays}.");
            }
            else
            {
                retainDays = retainDaysRaw.Value;
            }
        }

        return new SyncSettings(
            config.Bool(Section, "enabled") ?? DefaultEnabled,
            config.String(Section, "dir"),
            ceiling,
            scope,
            staleAfterDays,
            retainDays,
            problems);
    }

    /// <summary>Resolves the effective sync directory: the config override, or the home's default.</summary>
    public string ResolveDir(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Dir is { Length: > 0 } ? Dir : home.SyncDir;
    }
}
