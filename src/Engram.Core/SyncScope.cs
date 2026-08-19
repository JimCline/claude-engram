using System.Linq;

namespace Engram.Core;

public enum SyncScopeKind
{
    All,
    User,
    Repo,
}

/// <summary>
/// Zero, one, or more <c>repo_registry</c> rows matching a typed <c>repo:&lt;value&gt;</c> scope
/// (docs/memory-expansion/01-sync-spec.md, "Scoped export").
/// </summary>
public sealed record RepoScopeMatch(bool Found, string? RepoPath, string? Identity, IReadOnlyList<string> AmbiguousRepoPaths)
{
    public static readonly RepoScopeMatch None = new(false, null, null, []);

    public static RepoScopeMatch Ok(string repoPath, string identity) => new(true, repoPath, identity, []);

    public static RepoScopeMatch Ambiguous(IReadOnlyList<string> repoPaths) => new(false, null, null, repoPaths);
}

/// <summary>
/// The <c>[sync] scope</c> baseline (docs/memory-expansion/01-sync-spec.md, "Scoped export"):
/// which live facts are export-eligible before the per-fact <c>fact_sync_request</c> opt-in is
/// OR'd in. Shared by <see cref="SyncSettings"/> (the config default) and the CLI's
/// <c>--scope</c> override so there is one parser and one <c>repo_registry</c> resolver, not two
/// — the same "one implementation per behaviour" reasoning the rest of this codebase applies to
/// corroboration and replay.
/// </summary>
public static class SyncScope
{
    public const string RepoPrefix = "repo:";
    public const string Default = "all";

    /// <summary>
    /// Parses the raw <c>"all" | "user" | "repo:&lt;value&gt;"</c> surface. Never touches the
    /// database — resolving a <c>repo:</c> value against <c>repo_registry</c> is a separate,
    /// impure step (<see cref="ResolveRepo"/>), kept apart so this parse is a pure function.
    /// </summary>
    public static bool TryParse(string? text, out SyncScopeKind kind, out string? repoValue, out string? error)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == "all")
        {
            kind = SyncScopeKind.All;
            repoValue = null;
            error = null;
            return true;
        }

        if (trimmed == "user")
        {
            kind = SyncScopeKind.User;
            repoValue = null;
            error = null;
            return true;
        }

        if (trimmed.StartsWith(RepoPrefix, StringComparison.Ordinal) && trimmed.Length > RepoPrefix.Length)
        {
            kind = SyncScopeKind.Repo;
            repoValue = trimmed[RepoPrefix.Length..];
            error = null;
            return true;
        }

        kind = SyncScopeKind.All;
        repoValue = null;
        error = $"'{text}' is not a valid sync scope; expected 'all', 'user', or 'repo:<value>'.";
        return false;
    }

    /// <summary>
    /// Matches a typed <c>repo:&lt;value&gt;</c> value against enrolled repos: an exact
    /// <c>identity</c> match, or a <c>repo_path</c> whose trailing <c>/</c>-segment equals the
    /// value (the friendlier short form most people type, e.g. <c>acme-api</c> against
    /// <c>/projects/acme/code/acme-api</c>). A pure function over already-fetched rows, so it is
    /// tier-1 testable without a database.
    /// </summary>
    public static RepoScopeMatch ResolveRepo(IReadOnlyList<(string RepoPath, string Identity)> rows, string value)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(value);

        var matches = rows
            .Where(row => row.Identity == value || row.RepoPath.EndsWith("/" + value, StringComparison.Ordinal))
            .ToList();

        return matches.Count switch
        {
            0 => RepoScopeMatch.None,
            1 => RepoScopeMatch.Ok(matches[0].RepoPath, matches[0].Identity),
            _ => RepoScopeMatch.Ambiguous(matches.Select(m => m.RepoPath).ToList()),
        };
    }

    /// <summary>
    /// The scope-eligibility SQL fragment for a resolved scope kind, plus the resolved
    /// <c>repo_path</c> to bind as its parameter (repo scope only). A pure function of already-
    /// resolved inputs — no database access — so the fragments themselves are tier-1 testable.
    /// </summary>
    public static (string Clause, string? RepoPath) Clause(SyncScopeKind kind, string? resolvedRepoPath) => kind switch
    {
        SyncScopeKind.All => ("1=1", null),
        SyncScopeKind.User => ("f.scope = 'user'", null),
        SyncScopeKind.Repo => (
            "((f.path = $scopeRepoPath OR f.path LIKE $scopeRepoPath || '/%') "
                + "OR (f.scope = 'session' AND f.session_id IN "
                + "(SELECT id FROM session WHERE repo_path = $scopeRepoPath)))",
            resolvedRepoPath ?? throw new ArgumentException(
                "repo scope requires a resolved repo path.", nameof(resolvedRepoPath))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
