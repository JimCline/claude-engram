namespace Engram.Core.Tests;

public class NoHardcodedPathsTests
{
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

        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            if (string.Equals(Path.GetFullPath(file), allowedFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                foreach (var literal in ForbiddenLiterals)
                {
                    if (lines[lineNumber].Contains(literal, StringComparison.Ordinal))
                    {
                        violations.Add($"{file}:{lineNumber + 1}: contains forbidden literal '{literal}'");
                    }
                }
            }
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
