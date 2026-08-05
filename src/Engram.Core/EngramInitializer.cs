namespace Engram.Core;

public readonly record struct InitializedPath(string Path, bool Created);

public static class EngramInitializer
{
    public static IReadOnlyList<InitializedPath> Initialize(EngramHome home)
    {
        return
        [
            EnsureDirectory(home.Root),
            EnsureDirectory(home.ModelsDir),
            EnsureDirectory(home.QueueDir),
            EnsureDirectory(home.ReportDir),
            EnsureConfig(home.ConfigPath),
            EnsureDatabase(home),
        ];
    }

    /// <summary>
    /// Creates the database if absent and applies the seed corpus once (D10: a usable
    /// instance should not require the user to run anything else).
    /// </summary>
    private static InitializedPath EnsureDatabase(EngramHome home)
    {
        var existed = File.Exists(home.DatabasePath);

        using var connection = EngramDatabase.OpenInitialized(home);
        CannedFactSeeder.SeedOnce(connection, DateTimeOffset.UtcNow);

        return new InitializedPath(home.DatabasePath, Created: !existed);
    }

    private static InitializedPath EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return new InitializedPath(path, Created: false);
        }

        Directory.CreateDirectory(path);
        return new InitializedPath(path, Created: true);
    }

    private static InitializedPath EnsureConfig(string path)
    {
        if (File.Exists(path))
        {
            return new InitializedPath(path, Created: false);
        }

        AtomicFile.Write(path, DefaultConfig.Content + "\n");
        return new InitializedPath(path, Created: true);
    }
}
