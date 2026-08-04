using System.Text.RegularExpressions;

namespace Engram.Core.Tests;

public class NoHardcodedPathsTests
{
    private const string WaiverMarker = "engram-lint:allow";

    private static readonly Regex WaiverPattern = new(@"engram-lint:allow\(([^()]*)\)", RegexOptions.Compiled);

    private static readonly string[] ForbiddenLiterals =
    [
        ".engram",
        "ENGRAM_HOME",
        "SpecialFolder.UserProfile",
        "~/.claude",
        ".claude/settings.json",
    ];

    [Fact]
    public void SourceFiles_DoNotHardcodeHomePaths()
    {
        var repoRoot = FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        var allowedFile = Path.GetFullPath(Path.Combine(srcRoot, "Engram.Core", "EngramHome.cs"));

        var violations = new List<string>();
        var waivers = new List<(string File, int Line, string Reason)>();

        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            if (string.Equals(Path.GetFullPath(file), allowedFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                var line = lines[lineNumber];
                var matchedLiterals = ForbiddenLiterals.Where(literal => line.Contains(literal, StringComparison.Ordinal)).ToList();
                if (matchedLiterals.Count == 0)
                {
                    continue;
                }

                if (line.Contains(WaiverMarker, StringComparison.Ordinal))
                {
                    var waiverMatch = WaiverPattern.Match(line);
                    var reason = waiverMatch.Success ? waiverMatch.Groups[1].Value.Trim() : string.Empty;
                    if (reason.Length == 0)
                    {
                        violations.Add($"{file}:{lineNumber + 1}: waiver '{WaiverMarker}' requires a non-empty reason");
                        continue;
                    }

                    waivers.Add((file, lineNumber + 1, reason));
                    continue;
                }

                foreach (var literal in matchedLiterals)
                {
                    violations.Add($"{file}:{lineNumber + 1}: contains forbidden literal '{literal}'");
                }
            }
        }

        if (waivers.Count > 0)
        {
            var report = "Lint waivers honored:\n" +
                string.Join('\n', waivers.Select(w => $"{w.File}:{w.Line}: {w.Reason}"));

            Xunit.TestContext.Current?.TestOutputHelper?.WriteLine(report);
        }

        Assert.True(violations.Count == 0, "Hardcoded home-path literals found:\n" + string.Join('\n', violations));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string srcRoot)
    {
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(srcRoot, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("obj") || segments.Contains("bin"))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Engram.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (Engram.sln) by walking up from AppContext.BaseDirectory.");
    }
}
