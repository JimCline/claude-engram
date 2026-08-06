using System.Diagnostics;
using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// Reports what indexing would read in a directory, and what it would leave out.
/// </summary>
/// <remarks>
/// The counterpart to the filter's defaults erring toward exclusion: a rule that quietly drops
/// too much would otherwise look like a repo that simply has no code facts. This makes the
/// skipping legible before it becomes a store nobody can clean, since a fact once written is not
/// something <c>compact</c> is allowed to remove (D8).
/// </remarks>
internal static class ScanCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var showSkipped = false;
        var showFiles = false;
        string? target = null;

        foreach (var argument in rest)
        {
            switch (argument)
            {
                case "--skipped":
                    showSkipped = true;
                    break;
                case "--files":
                    showFiles = true;
                    break;
                default:
                    if (argument.StartsWith('-') || target is not null)
                    {
                        stderr.WriteLine($"error: unexpected argument '{argument}'");
                        return 1;
                    }

                    target = argument;
                    break;
            }
        }

        var root = Path.GetFullPath(target ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            stderr.WriteLine($"error: no directory at {root}");
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var settings = IndexingSettings.Read(ConfigFile.Load(home.ConfigPath));

        foreach (var problem in settings.Problems)
        {
            stderr.WriteLine($"warning: {problem}");
        }

        var stopwatch = Stopwatch.StartNew();
        var scan = RepoScanner.Scan(root, settings);
        stopwatch.Stop();

        stdout.WriteLine(root);
        stdout.WriteLine($"  {scan.Summary()} in {stopwatch.ElapsedMilliseconds} ms");

        if (scan.Source == ScanSource.DirectoryWalk && settings.UseGit)
        {
            stdout.WriteLine("  not a git checkout, so only the ignore patterns applied");
        }

        if (showSkipped)
        {
            WriteSkipped(root, settings, scan, stdout);
        }

        if (showFiles)
        {
            foreach (var file in scan.Files)
            {
                stdout.WriteLine($"  + {file}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Re-inspects to name the skipped files. The scan counts rather than collects them, because
    /// a repository can skip a hundred thousand files and nobody wants that list by default.
    /// </summary>
    private static void WriteSkipped(string root, IndexingSettings settings, RepoScan scan, TextWriter stdout)
    {
        if (scan.SkippedTotal == 0)
        {
            return;
        }

        var filter = new IndexFilter(settings);
        var listed = settings.UseGit ? new GitFileLister().List(root) : null;

        if (listed is null)
        {
            stdout.WriteLine("  (skipped files are only listed for a git checkout; the walk prunes whole directories)");
            return;
        }

        foreach (var relative in listed)
        {
            var verdict = filter.Inspect(relative, Path.Combine(root, relative));
            if (!verdict.Include)
            {
                stdout.WriteLine($"  - {relative}  ({verdict.Reason.ToString().ToLowerInvariant()})");
            }
        }
    }
}
