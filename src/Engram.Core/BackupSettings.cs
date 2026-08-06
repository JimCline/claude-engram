namespace Engram.Core;

/// <summary>
/// The <c>[backup]</c> section: how often a snapshot may be taken, and how many survive.
/// </summary>
/// <remarks>
/// <para><b>The interval is a ceiling, not a schedule.</b> A snapshot is taken only when the
/// fingerprint of authored truth has moved since the last one, so an idle day costs nothing —
/// no disk, no <c>VACUUM</c>, no entries to prune. A clock-driven backup would write the same
/// bytes twenty-four times over and then thin twenty-three of them back out, which is work
/// performed to undo itself.</para>
///
/// <para><b>Retention is generational because the failure modes are.</b> Losing an hour of facts
/// is an accident you notice immediately; losing a week of them is one you notice long after
/// twenty-four hourly snapshots have rolled past. Keeping the newest snapshot in each hour, then
/// each day, then each week spends a bounded number of files on a reach measured in months.</para>
/// </remarks>
public sealed record BackupSettings(
    bool Enabled,
    int IntervalMinutes,
    int KeepHourly,
    int KeepDaily,
    int KeepWeekly,
    IReadOnlyList<string> Problems)
{
    public const string Section = "backup";

    public const bool DefaultEnabled = true;

    /// <summary>
    /// The shortest gap between two snapshots.
    /// </summary>
    /// <remarks>
    /// An hour, because the unit of loss here is a working session rather than a transaction.
    /// Facts arrive in bursts while an agent works and not at all in between, so a finer interval
    /// buys resolution nobody can use while a coarser one can drop a whole afternoon.
    /// </remarks>
    public const int DefaultIntervalMinutes = 60;

    public const int DefaultKeepHourly = 24;
    public const int DefaultKeepDaily = 7;
    public const int DefaultKeepWeekly = 4;

    public static BackupSettings Default { get; } = new(
        DefaultEnabled,
        DefaultIntervalMinutes,
        DefaultKeepHourly,
        DefaultKeepDaily,
        DefaultKeepWeekly,
        []);

    public static BackupSettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();

        return new BackupSettings(
            config.Bool(Section, "enabled") ?? DefaultEnabled,
            Positive(config, "interval_minutes", DefaultIntervalMinutes, problems),
            Positive(config, "keep_hourly", DefaultKeepHourly, problems),
            Positive(config, "keep_daily", DefaultKeepDaily, problems),
            Positive(config, "keep_weekly", DefaultKeepWeekly, problems),
            problems);
    }

    /// <remarks>
    /// Zero is rejected for the retention counts rather than honoured. A count of nought reads
    /// like "keep none", and a backup system that deletes every snapshot on the strength of one
    /// mistyped line is the failure it exists to prevent.
    /// </remarks>
    private static int Positive(ConfigFile config, string key, int fallback, List<string> problems)
    {
        var value = config.Int(Section, key);
        if (value is null)
        {
            return fallback;
        }

        if (value < 1)
        {
            problems.Add($"[{Section}] {key} must be at least 1; using {fallback}.");
            return fallback;
        }

        return value.Value;
    }
}
