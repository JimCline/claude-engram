namespace Engram.Core;

public sealed class EngramHome
{
    public const string DirectoryName = ".engram";

    public string Root { get; }
    public string DatabasePath { get; }
    public string ConfigPath { get; }
    public string LogPath { get; }
    public string ModelsDir { get; }
    public string QueueDir { get; }
    public string ReportDir { get; }

    /// <summary>
    /// Optional native libraries — <c>sqlite-vec</c> and llama.cpp — fetched by
    /// <c>engram init --with-embeddings</c> (D1).
    /// </summary>
    /// <remarks>
    /// Deliberately not created by <c>init</c>, unlike every other directory here. An empty
    /// <c>lib/</c> claims a feature is installed when it is not, and the two states this
    /// system has to distinguish are "embeddings are off" and "embeddings are on and
    /// broken". A directory that exists either way erases that distinction for `doctor`.
    /// </remarks>
    public string LibDir { get; }

    /// <summary>
    /// Snapshots of the store, and the append-only fact journal.
    /// </summary>
    /// <remarks>
    /// Inside the home, which is the honest limitation to state up front: this defends against
    /// logical loss — a migration that goes wrong, a bad <c>forget</c>, corruption — and not
    /// against the directory itself going away. Putting it outside would mean inventing a second
    /// location to resolve, own, and uninstall, for a class of failure a real backup tool already
    /// covers better.
    /// </remarks>
    public string BackupDir { get; }

    /// <summary>
    /// Per-identity index lock files (§6.4), one per repo currently being indexed.
    /// </summary>
    /// <remarks>
    /// Derived state, like <see cref="BackupDir"/>: not created by <c>init</c>, only lazily by
    /// <see cref="IndexLock"/> on first claim, since a store that never runs a concurrent index
    /// never needs it.
    /// </remarks>
    public string IndexLockDir { get; }

    /// <summary>
    /// Default location of the cross-machine sync directory (docs/gp-adoption/01-sync-spec.md)
    /// — <c>&lt;machine-id&gt;/&lt;seq&gt;.jsonl</c> chunks plus the local <c>machine-id</c>
    /// discriminator file live under here.
    /// </summary>
    /// <remarks>
    /// Derived state, like <see cref="BackupDir"/>: not created by <c>init</c>, only lazily on
    /// first <c>sync export</c>. <c>[sync] dir</c> in config overrides this default; callers
    /// resolve that override themselves rather than this type reaching into <c>ConfigFile</c>,
    /// which is not this type's job.
    /// </remarks>
    public string SyncDir { get; }

    /// <summary>
    /// What ggml-metal reported about this machine at the last local model load (D28).
    /// </summary>
    /// <remarks>
    /// Derived state (D8): any load regenerates it whole, so deleting it costs nothing and
    /// <c>repair</c> and <c>compact</c> have no business in it. It exists because the fact it
    /// carries belongs to the process that loaded llama.cpp rather than to the binary asking,
    /// and <c>doctor</c> may not find it out for itself — finding out means loading the weights.
    /// </remarks>
    public string MetalRecordPath { get; }

    /// <summary>
    /// What the running server's embedding backlog is doing, for readers outside that process.
    /// </summary>
    /// <remarks>
    /// Derived state (D8), and only the part the store cannot answer: liveness, rate, what is being
    /// embedded, and why a pass stopped. Counts stay in the database, which is their authority.
    /// </remarks>
    public string EmbeddingProgressPath { get; }

    /// <summary>
    /// What the running server's background freshness loop is doing, for readers outside that
    /// process.
    /// </summary>
    /// <remarks>
    /// Derived state (D8), the third instance of the <c>embedding.json</c>/<c>metal.json</c>
    /// pattern (spec §6.8): liveness, which repo is being freshened, and why a tick stopped. How
    /// many repos are due stays in the database, which is their authority.
    /// </remarks>
    public string IndexProgressPath { get; }

    /// <summary>
    /// Which MCP tool permissions we granted in Claude Code's settings, so the uninstaller can
    /// take back exactly those and nothing the user wrote themselves.
    /// </summary>
    public string GrantedPermissionsPath { get; }

    /// <summary>
    /// Sessions <c>memory-guard</c> has already nudged once this run, one <c>session_id</c> per
    /// line.
    /// </summary>
    public string MemoryGuardStatePath { get; }

    /// <summary>
    /// Sessions <c>lookup-nudge</c> has already nudged once this run, one <c>session_id</c> per
    /// line. Separate from <see cref="MemoryGuardStatePath"/> so the two nudges never spend each
    /// other's one-shot.
    /// </summary>
    public string LookupNudgeStatePath { get; }

    /// <summary>
    /// Claude Code's user-scope settings file. It is outside the Engram home on purpose — it
    /// belongs to Claude Code — but it is resolved here because this is the only place allowed
    /// to turn a home directory into a path.
    /// </summary>
    public string ClaudeSettingsPath { get; }

    /// <summary>
    /// Claude Code's per-project directories, where its file-based auto-memory lives
    /// (<c>&lt;projects&gt;/&lt;slug&gt;/memory/*.md</c>). Outside the Engram home for the same
    /// reason <see cref="ClaudeSettingsPath"/> is — it belongs to Claude Code, and this is the
    /// only place allowed to turn a home directory into a path.
    /// </summary>
    public string ClaudeProjectsDir { get; }

    private EngramHome(string root, string userProfileDirectory)
    {
        Root = root;
        DatabasePath = Path.Combine(root, "engram.db");
        ConfigPath = Path.Combine(root, "config.toml");
        LogPath = Path.Combine(root, "engram.log");
        ModelsDir = Path.Combine(root, "models");
        QueueDir = Path.Combine(root, "queue");
        ReportDir = Path.Combine(root, "report");
        LibDir = Path.Combine(root, "lib");
        BackupDir = Path.Combine(root, "backups");
        IndexLockDir = Path.Combine(root, "locks");
        SyncDir = Path.Combine(root, "sync");
        MetalRecordPath = Path.Combine(root, "metal.json");
        EmbeddingProgressPath = Path.Combine(root, "embedding.json");
        IndexProgressPath = Path.Combine(root, "indexing.json");
        GrantedPermissionsPath = Path.Combine(root, "granted-permissions.json");
        MemoryGuardStatePath = Path.Combine(root, "memory-guard.state");
        LookupNudgeStatePath = Path.Combine(root, "lookup-nudge.state");
        ClaudeSettingsPath = Path.Combine(userProfileDirectory, ".claude", "settings.json");
        ClaudeProjectsDir = Path.Combine(userProfileDirectory, ".claude", "projects");
    }

    public static EngramHome Resolve(
        string? explicitPath,
        IReadOnlyDictionary<string, string?> environment,
        string userProfileDirectory,
        string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(userProfileDirectory))
        {
            throw new ArgumentException("User profile directory must not be null or whitespace.", nameof(userProfileDirectory));
        }

        if (string.IsNullOrWhiteSpace(currentDirectory))
        {
            throw new ArgumentException("Current directory must not be null or whitespace.", nameof(currentDirectory));
        }

        string chosen;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            chosen = explicitPath;
        }
        else if (environment.TryGetValue("ENGRAM_HOME", out var envHome) && !string.IsNullOrWhiteSpace(envHome))
        {
            chosen = envHome;
        }
        else
        {
            chosen = Path.Combine(userProfileDirectory, DirectoryName);
        }

        var expanded = ExpandTilde(chosen, userProfileDirectory);
        var rooted = Path.IsPathRooted(expanded) ? expanded : Path.Combine(currentDirectory, expanded);
        var fullPath = Path.GetFullPath(rooted);
        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 0)
        {
            normalized = fullPath;
        }

        return new EngramHome(normalized, userProfileDirectory);
    }

    public static EngramHome ResolveFromProcess(string? explicitPath)
    {
        var environment = new Dictionary<string, string?>
        {
            ["ENGRAM_HOME"] = Environment.GetEnvironmentVariable("ENGRAM_HOME"),
        };

        return Resolve(explicitPath, environment, UserProfileDirectory(), Environment.CurrentDirectory);
    }

    public static string UserProfileDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ExpandTilde(string path, string userProfileDirectory)
    {
        if (path == "~")
        {
            return userProfileDirectory;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(userProfileDirectory, path[2..]);
        }

        return path;
    }
}
