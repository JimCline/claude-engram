namespace Engram.Core.Tests;

/// <summary>
/// Holds spec §2.0.2 ruling 1: making <see cref="RecallRanker"/> <c>public</c> — required because
/// <c>EngramMcpTools</c> lives in a different assembly than this one — means accessibility no
/// longer even appears to guard "nothing else may produce ranking SQL" (§2.1). It never really did:
/// <c>internal</c> would not have stopped a second class inside <c>Engram.Core</c> from writing its
/// own ranking SQL either. This test is what enforces the rule now.
/// </summary>
/// <remarks>
/// Keyed on <c>is_corroborated</c> rather than <c>bm25(</c> or <c>fact_token</c>: both of those
/// legitimately appear elsewhere — <c>FactStore.SearchRanked</c> and <c>FactTokenIndex</c> — so
/// keying on either would false-positive on code this test must leave alone. <c>is_corroborated</c>
/// names a column only the fused ranking statement computes.
/// </remarks>
public class NoSecondRankerTests
{
    private const string RankingStatementMarker = "is_corroborated";

    [Fact]
    public void TheFusedRankingStatement_ExistsInExactlyOneFile()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");

        var matches = EnumerateSourceFiles(srcRoot)
            .Where(file => File.ReadAllText(file).Contains(RankingStatementMarker, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(srcRoot, file))
            .ToList();

        Assert.True(
            matches.Count == 1,
            $"Expected exactly one file to contain '{RankingStatementMarker}' (the fused ranking "
                + $"statement built by RecallRanker), found {matches.Count}:\n  "
                + string.Join("\n  ", matches));
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
