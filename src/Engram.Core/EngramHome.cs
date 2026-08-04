namespace Engram.Core;

public sealed class EngramHome
{
    public string Root { get; }
    public string DatabasePath { get; }
    public string ConfigPath { get; }
    public string LogPath { get; }
    public string ModelsDir { get; }
    public string QueueDir { get; }
    public string ReportDir { get; }

    private EngramHome(string root)
    {
        Root = root;
        DatabasePath = Path.Combine(root, "engram.db");
        ConfigPath = Path.Combine(root, "config.toml");
        LogPath = Path.Combine(root, "engram.log");
        ModelsDir = Path.Combine(root, "models");
        QueueDir = Path.Combine(root, "queue");
        ReportDir = Path.Combine(root, "report");
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
            chosen = Path.Combine(userProfileDirectory, ".engram");
        }

        var expanded = ExpandTilde(chosen, userProfileDirectory);
        var rooted = Path.IsPathRooted(expanded) ? expanded : Path.Combine(currentDirectory, expanded);
        var fullPath = Path.GetFullPath(rooted);
        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 0)
        {
            normalized = fullPath;
        }

        return new EngramHome(normalized);
    }

    public static EngramHome ResolveFromProcess(string? explicitPath)
    {
        var environment = new Dictionary<string, string?>
        {
            ["ENGRAM_HOME"] = Environment.GetEnvironmentVariable("ENGRAM_HOME"),
        };
        var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Resolve(explicitPath, environment, userProfileDirectory, Environment.CurrentDirectory);
    }

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
