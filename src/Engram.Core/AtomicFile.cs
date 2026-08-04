namespace Engram.Core;

public static class AtomicFile
{
    public static PendingWrite Prepare(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            return new PendingWrite(path, tempPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    public static void Write(string path, string content)
    {
        using var pending = Prepare(path, content);
        pending.Commit();
    }
}

public sealed class PendingWrite : IDisposable
{
    private readonly string _tempPath;
    private bool _committed;
    private bool _disposed;

    internal PendingWrite(string targetPath, string tempPath)
    {
        TargetPath = targetPath;
        _tempPath = tempPath;
    }

    public string TargetPath { get; }

    public void Commit()
    {
        File.Move(_tempPath, TargetPath, overwrite: true);
        _committed = true;
    }

    public void Dispose()
    {
        if (_disposed || _committed)
        {
            _disposed = true;
            return;
        }

        _disposed = true;

        try
        {
            if (File.Exists(_tempPath))
            {
                File.Delete(_tempPath);
            }
        }
        catch
        {
        }
    }
}
