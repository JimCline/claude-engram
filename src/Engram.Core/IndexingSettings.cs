namespace Engram.Core;

/// <summary>
/// The <c>[indexing]</c> section: what gets indexed, and what is skipped before it costs anything.
/// </summary>
/// <remarks>
/// <para><b>The defaults exclude when unsure, and that asymmetry is the whole design.</b> A file
/// wrongly indexed becomes facts, and facts are append-only — <c>compact</c> and <c>repair</c> are
/// forbidden from deleting a fact body (D8), and nothing downstream can tell a fact derived from a
/// minified bundle from one derived from real source. So the only cure for a bad include is
/// <c>forget</c>, by hand, after the noise has already been served to a model. A bad exclude costs
/// one line of config and a re-index.</para>
///
/// <para>Every skip is counted and reported rather than silent, so an over-eager rule shows up as
/// a number in <c>doctor</c> instead of as a repo that mysteriously has no code facts.</para>
/// </remarks>
public sealed record IndexingSettings(
    bool AutoIndexOnSessionStart,
    int MaxSyncIndexMs,
    IReadOnlyList<string> Ignore,
    long MaxFileBytes,
    int MaxMeanLineBytes,
    bool UseGit,
    IReadOnlyList<string> Problems)
{
    public const string Section = "indexing";

    public const bool DefaultAutoIndexOnSessionStart = true;
    public const int DefaultMaxSyncIndexMs = 1500;

    /// <summary>
    /// Files above this are skipped whole.
    /// </summary>
    /// <remarks>
    /// A megabyte of source is not source. Generated parsers, vendored bundles, fixture dumps and
    /// checked-in datasets all live above this line, and hand-written code essentially never does.
    /// </remarks>
    public const long DefaultMaxFileBytes = 1_000_000;

    /// <summary>
    /// A file averaging more than this per line is treated as generated.
    /// </summary>
    /// <remarks>
    /// <para>This is the minified-and-bundled detector, and it is what catches the files that pass
    /// every other check: a webpack bundle is valid UTF-8 JavaScript with a real extension, tracked
    /// by git, sometimes under the size cap — and one line long.</para>
    ///
    /// <para>Chosen from a measurement rather than by feel. Across this repository's 175 tracked
    /// text files the mean line is 38 bytes at p50, 49 at p90, 68 at p99, and 170 at the very worst
    /// (a file of long string literals). A minified bundle runs to thousands. 400 sits in the gap
    /// with better than 2× headroom over the worst real file and an order of magnitude of margin
    /// below anything generated — the point being that there <i>is</i> a gap, so the threshold is
    /// not a knife edge.</para>
    /// </remarks>
    public const int DefaultMaxMeanLineBytes = 400;

    /// <summary>How much of a file is read to classify it.</summary>
    /// <remarks>
    /// The same 8 KB git reads for its own binary test. Enough to be certain about content, small
    /// enough that classifying a large tree is bounded by syscalls rather than by bytes.
    /// </remarks>
    public const int HeadBytes = 8192;

    /// <summary>
    /// Patterns applied on top of git's own opinion, and alone where there is no checkout.
    /// </summary>
    /// <remarks>
    /// <para>This list is deliberately not a general-purpose <c>.gitignore</c>. In a checkout it is
    /// nearly redundant — git already excludes all of this — and its real job is the fallback,
    /// where there is no better authority than a list.</para>
    ///
    /// <para>Which is why it covers more than one ecosystem. Measured across a workspace of 38
    /// repositories: the directories that were not checkouts walked 25,092 and 23,828 files, and
    /// the cost was almost entirely <c>stable-audio-tools/.venv</c> (33,000 files),
    /// <c>venv/lib</c> (26,074) and Swift's <c>.build</c> (11,780) — none of which the original
    /// four .NET-and-JavaScript patterns matched. A list that only knows the languages its author
    /// happened to be using is the failure mode this is written against.</para>
    /// </remarks>
    public static IReadOnlyList<string> DefaultIgnore { get; } =
    [
        "**/.git/**",

        // .NET
        "**/bin/**",
        "**/obj/**",

        // JavaScript
        "**/node_modules/**",
        "**/.next/**",

        // Python
        "**/.venv/**",
        "**/venv/**",
        "**/__pycache__/**",
        "**/*.egg-info/**",
        "**/.mypy_cache/**",
        "**/.pytest_cache/**",

        // Swift, Xcode
        "**/.build/**",
        "**/DerivedData/**",
        "**/Pods/**",

        // Rust, Go, PHP
        "**/target/**",
        "**/vendor/**",

        // Language-agnostic build and cache output
        "**/dist/**",
        "**/build/**",
        "**/.cache/**",
        "**/coverage/**",
    ];

    public static IndexingSettings Default { get; } = new(
        DefaultAutoIndexOnSessionStart,
        DefaultMaxSyncIndexMs,
        DefaultIgnore,
        DefaultMaxFileBytes,
        DefaultMaxMeanLineBytes,
        UseGit: true,
        Problems: []);

    public static IndexingSettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();

        var ignore = config.Strings(Section, "ignore");
        if (ignore.Count == 0 && config.Raw(Section, "ignore") is null)
        {
            ignore = DefaultIgnore;
        }

        return new IndexingSettings(
            config.Bool(Section, "auto_index_on_session_start") ?? DefaultAutoIndexOnSessionStart,
            Positive(config, "max_sync_index_ms", DefaultMaxSyncIndexMs, problems),
            ignore,
            Positive(config, "max_file_bytes", (int)DefaultMaxFileBytes, problems),
            Positive(config, "max_mean_line_bytes", DefaultMaxMeanLineBytes, problems),
            config.Bool(Section, "use_git") ?? true,
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
