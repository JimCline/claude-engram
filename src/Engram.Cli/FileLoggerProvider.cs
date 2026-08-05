using Microsoft.Extensions.Logging;

namespace Engram.Cli;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly string _path;

    public FileLoggerProvider(string path)
    {
        _path = path;

        // Kestrel logs while binding, so the first write lands before anything else has
        // had a reason to create the home directory. A missing directory throws from
        // inside a daemon whose stderr is /dev/null, which reaches the user only as
        // `engram start` timing out with no diagnostic anywhere. Sandbox homes come from
        // mktemp -d and so always exist, which is why no test saw this.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        RotateIfLarge(path);
    }

    // Log level alone does not bound this file: a wedged or crash-looping server writes
    // warnings continuously, which is exactly when nobody is watching disk. One rotation
    // at startup caps the pair at ~2 MB and keeps the previous run available, which is the
    // run you want when diagnosing why the current one had to be restarted.
    private static void RotateIfLarge(string path)
    {
        const long MaxBytes = 1024 * 1024;

        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(path, path + ".1", overwrite: true);
            }
        }
        catch (IOException)
        {
            // A log we cannot rotate is not a reason to refuse to serve.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTimeOffset.UtcNow:o} [{logLevel}] {categoryName}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line);
        }
    }
}
