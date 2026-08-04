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
        ];
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
