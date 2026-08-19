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
    IReadOnlyList<string> Problems)
{
    public const string Section = "sync";

    public const bool DefaultEnabled = false;

    public static SyncSettings Default { get; } =
        new(DefaultEnabled, null, CloseResolver.DefaultRetryCeiling, SyncScope.Default, []);

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

        return new SyncSettings(
            config.Bool(Section, "enabled") ?? DefaultEnabled,
            config.String(Section, "dir"),
            ceiling,
            scope,
            problems);
    }

    /// <summary>Resolves the effective sync directory: the config override, or the home's default.</summary>
    public string ResolveDir(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Dir is { Length: > 0 } ? Dir : home.SyncDir;
    }
}
