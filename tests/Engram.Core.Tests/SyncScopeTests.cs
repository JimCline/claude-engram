using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// Scope parsing, repo-registry resolution, and clause selection
/// (docs/memory-expansion/01-sync-spec.md, "Scoped export") — tested as the pure functions they
/// are, no database.
/// </summary>
public class SyncScopeTests
{
    [Theory]
    [InlineData(null, SyncScopeKind.All, null)]
    [InlineData("", SyncScopeKind.All, null)]
    [InlineData("all", SyncScopeKind.All, null)]
    [InlineData("user", SyncScopeKind.User, null)]
    [InlineData("repo:engram", SyncScopeKind.Repo, "engram")]
    public void TryParse_AcceptsTheDocumentedForms(string? text, SyncScopeKind expectedKind, string? expectedValue)
    {
        var ok = SyncScope.TryParse(text, out var kind, out var repoValue, out var error);

        Assert.True(ok);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedValue, repoValue);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("repo:")]
    [InlineData("bogus")]
    [InlineData("Repo:engram")]
    public void TryParse_RejectsAnythingElse(string text)
    {
        var ok = SyncScope.TryParse(text, out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    /// <summary>
    /// Falsification: deleting the `Length > RepoPrefix.Length` guard would let a bare "repo:"
    /// parse as a repo scope with an empty value, which then has nothing to resolve against.
    /// This test fails red under that deletion.
    /// </summary>
    [Fact]
    public void Falsification_RemovingTheEmptyValueGuard_WouldAcceptABareRepoPrefix()
    {
        var ok = SyncScope.TryParse("repo:", out var kind, out _, out _);

        Assert.False(ok);
        Assert.NotEqual(SyncScopeKind.Repo, kind);
    }

    [Fact]
    public void ResolveRepo_MatchesByExactIdentity()
    {
        var rows = new List<(string RepoPath, string Identity)>
        {
            ("/home/x/repos/engram", "engram"),
            ("/home/x/repos/other", "other"),
        };

        var match = SyncScope.ResolveRepo(rows, "engram");

        Assert.True(match.Found);
        Assert.Equal("/home/x/repos/engram", match.RepoPath);
        Assert.Equal("engram", match.Identity);
    }

    [Fact]
    public void ResolveRepo_MatchesByTrailingPathSegment()
    {
        var rows = new List<(string RepoPath, string Identity)>
        {
            ("/home/x/repos/engram", "a1b2c3"),
        };

        var match = SyncScope.ResolveRepo(rows, "engram");

        Assert.True(match.Found);
        Assert.Equal("/home/x/repos/engram", match.RepoPath);
    }

    [Fact]
    public void ResolveRepo_ZeroMatches_ReportsNotFound()
    {
        var match = SyncScope.ResolveRepo([], "engram");

        Assert.False(match.Found);
        Assert.Empty(match.AmbiguousRepoPaths);
    }

    [Fact]
    public void ResolveRepo_MultipleMatches_ReportsAmbiguous()
    {
        var rows = new List<(string RepoPath, string Identity)>
        {
            ("/home/x/repos/engram", "engram"),
            ("/home/y/other/engram", "engram-fork"),
        };

        var match = SyncScope.ResolveRepo(rows, "engram");

        Assert.False(match.Found);
        Assert.Equal(2, match.AmbiguousRepoPaths.Count);
    }

    /// <summary>
    /// Falsification: matching on Contains rather than exact-identity-or-trailing-segment would
    /// let "gram" resolve against "engram", silently widening every repo scope. This test fails
    /// red under that substitution.
    /// </summary>
    [Fact]
    public void Falsification_ResolveRepo_APartialSegmentDoesNotMatch()
    {
        var rows = new List<(string RepoPath, string Identity)>
        {
            ("/home/x/repos/engram", "abc123"),
        };

        var match = SyncScope.ResolveRepo(rows, "gram");

        Assert.False(match.Found);
    }

    [Fact]
    public void Clause_All_IsUnconditional()
    {
        var (clause, repoPath) = SyncScope.Clause(SyncScopeKind.All, null);

        Assert.Equal("1=1", clause);
        Assert.Null(repoPath);
    }

    [Fact]
    public void Clause_User_FiltersOnFactScope()
    {
        var (clause, repoPath) = SyncScope.Clause(SyncScopeKind.User, null);

        Assert.Contains("f.scope = 'user'", clause);
        Assert.Null(repoPath);
    }

    [Fact]
    public void Clause_Repo_CoversBothCodePathsAndSessionRepoPath()
    {
        var (clause, repoPath) = SyncScope.Clause(SyncScopeKind.Repo, "/home/x/repos/engram");

        Assert.Contains("f.path = $scopeRepoPath", clause);
        Assert.Contains("f.path LIKE $scopeRepoPath || '/%'", clause);
        Assert.Contains("FROM session WHERE repo_path = $scopeRepoPath", clause);
        Assert.Contains("f.scope = 'session'", clause);
        Assert.Equal("/home/x/repos/engram", repoPath);
    }

    [Fact]
    public void Clause_Repo_WithoutAResolvedPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => SyncScope.Clause(SyncScopeKind.Repo, null));
    }
}
