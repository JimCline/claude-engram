using System.Diagnostics;

namespace Engram.Core;

internal static class DurableAppend
{
    private const int MaxRetryDelayMs = 20;

    // FileMode.Append is seek-then-write, not POSIX O_APPEND: two processes can resolve the
    // same end-of-file offset and one silently overwrites the other's record. FileShare.None
    // turns that lost update into a refused open (IOException), which the retry loop below
    // treats as contention to back off from instead of a race to lose. Callers must still hold
    // their own lock around this call to close the same gap between threads in this process,
    // which don't get that refusal from the OS.
    public static void TryAppend(string path, byte[] payload, TimeSpan retryBudget)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: payload.Length);
                stream.Write(payload, 0, payload.Length);
                return;
            }
            catch (IOException) when (elapsed.Elapsed < retryBudget)
            {
                Thread.Sleep(Random.Shared.Next(1, MaxRetryDelayMs));
            }
            catch (UnauthorizedAccessException) when (elapsed.Elapsed < retryBudget)
            {
                Thread.Sleep(Random.Shared.Next(1, MaxRetryDelayMs));
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
