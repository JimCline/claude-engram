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
    public string UserFactsDir { get; }

    /// <summary>
    /// Which MCP tool permissions we granted in Claude Code's settings, so the uninstaller can
    /// take back exactly those and nothing the user wrote themselves.
    /// </summary>
    public string GrantedPermissionsPath { get; }

    /// <summary>
    /// Claude Code's user-scope settings file. It is outside the Engram home on purpose — it
    /// belongs to Claude Code — but it is resolved here because this is the only place allowed
    /// to turn a home directory into a path.
    /// </summary>
    public string ClaudeSettingsPath { get; }

    private EngramHome(string root, string userProfileDirectory)
    {
        Root = root;
        DatabasePath = Path.Combine(root, "engram.db");
        ConfigPath = Path.Combine(root, "config.toml");
        LogPath = Path.Combine(root, "engram.log");
        ModelsDir = Path.Combine(root, "models");
        QueueDir = Path.Combine(root, "queue");
        ReportDir = Path.Combine(root, "report");
        UserFactsDir = Path.Combine(root, "user-facts");
        GrantedPermissionsPath = Path.Combine(root, "granted-permissions.json");
        ClaudeSettingsPath = Path.Combine(userProfileDirectory, ".claude", "settings.json");
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
