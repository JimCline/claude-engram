using System.Diagnostics;

namespace Engram.Core;

/// <summary>Lists the files a repository considers its own, or null if it cannot say.</summary>
public interface IFileLister
{
    /// <summary>
    /// Repo-relative paths, or <see langword="null"/> when this lister does not apply — the
    /// directory is not a checkout, or the tool is missing.
    /// </summary>
    IReadOnlyList<string>? List(string root);
}

/// <summary>Where the file list came from. Reported, because it changes what was excluded.</summary>
public enum ScanSource
{
    Git,
    DirectoryWalk,
}

public sealed record RepoScan(
    ScanSource Source,
    IReadOnlyList<string> Files,
    IReadOnlyDictionary<SkipReason, int> Skipped)
{
    public int SkippedTotal => Skipped.Values.Sum();

    /// <summary>A one-line summary for <c>doctor</c> and the indexer's log.</summary>
    public string Summary()
    {
        var detail = Skipped
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{pair.Value} {pair.Key.ToString().ToLowerInvariant()}");

        var source = Source == ScanSource.Git ? "git" : "directory walk";
        var skipped = string.Join(", ", detail);

        return skipped.Length == 0
            ? $"{Files.Count} files via {source}"
            : $"{Files.Count} files via {source}, skipped {skipped}";
    }
}

/// <summary>
/// Asks git which files belong to a repository, and walks the directory only when it cannot.
/// </summary>
/// <remarks>
/// <para><b>git is the authority wherever there is a checkout.</b> <c>git ls-files</c> already
/// excludes build output, dependency directories, caches and temporary files — correctly, per
/// nested <c>.gitignore</c>, per <c>.git/info/exclude</c>, and per the developer's global ignore
/// file. Every one of those is a decision the developer already made about their own tree, and a
/// hand-maintained exclusion list inside Engram would be a worse, staler copy of a file the repo
/// already ships.</para>
///
/// <para>Tracked <i>and</i> untracked-but-not-ignored, because a file written five minutes ago is
/// exactly the file a coding agent is about to be asked about. Ignored files are the ones left
/// out, which is the whole point.</para>
///
/// <para>The configured globs still apply on top. git's opinion is about what belongs in the
/// repository, not about what is worth reading — a vendored bundle can be committed deliberately
/// and still be junk to index.</para>
/// </remarks>
public static class RepoScanner
{
    public static RepoScan Scan(string root, IndexingSettings settings, IFileLister? lister = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(settings);

        var filter = new IndexFilter(settings);
        var full = Path.GetFullPath(root);

        var listed = settings.UseGit ? (lister ?? new GitFileLister()).List(full) : null;
        var source = listed is null ? ScanSource.DirectoryWalk : ScanSource.Git;
        var candidates = listed ?? Walk(full, filter);

        var files = new List<string>();
        var skipped = new Dictionary<SkipReason, int>();

        foreach (var relative in candidates.Distinct(StringComparer.Ordinal))
        {
            var verdict = filter.Inspect(relative, Path.Combine(full, relative));
            if (verdict.Include)
            {
                files.Add(relative);
            }
            else
            {
                skipped[verdict.Reason] = skipped.GetValueOrDefault(verdict.Reason) + 1;
            }
        }

        files.Sort(StringComparer.Ordinal);

        return new RepoScan(source, files, skipped);
    }

    /// <summary>
    /// The fallback for a directory that is not a checkout.
    /// </summary>
    /// <remarks>
    /// Pruned at the directory rather than filtered at the file, so an ignored <c>node_modules</c>
    /// costs one pattern match instead of a hundred thousand — which is the difference between a
    /// scan that finishes and one that does not.
    /// </remarks>
    private static List<string> Walk(string root, IndexFilter filter)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');

                if (Directory.Exists(entry))
                {
                    // Match the directory both bare and with a trailing slash, so "**/bin/**"
                    // prunes "bin" itself rather than only the files inside it.
                    if (!filter.IsIgnored(relative) && !filter.IsIgnored(relative + "/"))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                found.Add(relative);
            }
        }

        return found;
    }
}

/// <summary>Runs <c>git ls-files</c> in the target directory.</summary>
public sealed class GitFileLister : IFileLister
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public IReadOnlyList<string>? List(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!Directory.Exists(root))
        {
            return null;
        }

        // -z because a path may legally contain a newline, and splitting on one would invent
        // two files that do not exist.
        var output = Run(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard");

        return output?
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    internal static string? Run(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            // Read before waiting: a large repository fills the pipe buffer, and a process
            // blocked on a full stdout never exits, so waiting first would deadlock until the
            // timeout on exactly the repositories this matters most for.
            var stdout = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            // Not a checkout, or git is unhappy. Either way the caller walks the directory.
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // git is not installed. A perfectly ordinary machine state, not an error.
            return null;
        }
    }
}
