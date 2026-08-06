using System.Text.RegularExpressions;

namespace Engram.Core.Tests;

/// <summary>
/// Holds the line D1 draws around serialization now that the compiler cannot.
/// </summary>
/// <remarks>
/// The AOT publish used to enforce this on its own: a reflection-based
/// <c>JsonSerializer.Serialize</c> anywhere in Engram raised IL2026 and IL3050, warnings are
/// errors, and the build stopped. Referencing LLamaSharp put three unreachable instances of those
/// same warnings into the publish, and NoWarn is per-project rather than per-assembly, so
/// silencing theirs silences ours. This is what ours costs now.
///
/// It matches on text rather than on syntax because it is guarding a spelling: every call has to
/// name a generated context, and a call that does not is the exact shape that compiles, publishes,
/// and then throws at runtime on the one machine where the type was trimmed away.
/// </remarks>
public class NoReflectionJsonTests
{
    private static readonly Regex SerializerCall = new(
        @"JsonSerializer\.(Serialize|Deserialize)\w*\s*(?:<[^>()]*>)?\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void EveryJsonSerializerCall_NamesAGeneratedContext()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var violations = new List<string>();
        var checked_ = 0;

        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!SerializerCall.IsMatch(lines[i]))
                {
                    continue;
                }

                checked_++;

                // The argument may sit on the following line when the call is wrapped, so the
                // window is the call and its continuation rather than one line.
                var window = lines[i] + (i + 1 < lines.Length ? lines[i + 1] : string.Empty);
                if (!window.Contains("JsonContext.Default.", StringComparison.Ordinal)
                    && !window.Contains("typeInfo", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetRelativePath(srcRoot, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            checked_ > 0,
            "Found no JsonSerializer calls at all, so this test proves nothing — the pattern has "
                + "probably drifted from how the calls are now written.");

        Assert.True(
            violations.Count == 0,
            "Reflection-based JSON is AOT-hostile and no longer fails the build, because NoWarn in "
                + "Engram.Cli.csproj covers IL2026/IL3050 for LLamaSharp's sake. Pass a "
                + "source-generated JsonTypeInfo:\n  " + string.Join("\n  ", violations));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string srcRoot)
    {
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var segments = Path.GetRelativePath(srcRoot, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

        throw new InvalidOperationException(
            "Could not locate repo root (Engram.sln) by walking up from AppContext.BaseDirectory.");
    }
}
