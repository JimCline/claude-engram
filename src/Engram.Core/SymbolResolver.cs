using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>Which fallback tier produced a symbol match — always the same order (§3.4).</summary>
public enum SymbolMatchTier
{
    Exact,
    CaseInsensitive,
    Substring,
}

/// <summary>One symbol entity a name resolved to: its address, its bare name, and how it matched.</summary>
public sealed record SymbolMatch(string Path, string Name, SymbolMatchTier Tier);

/// <summary>
/// The one name→declaration-site lookup (spec §3.4, code-navigation-spec.md). `defined_at`
/// calls this to answer "where is Z defined", and Phase 3's query-time callee resolution
/// (D72, §5.2) binds a call site's written name through the same lookup — never a second
/// implementation, which is D30's argument restated: two resolvers diverge the first time
/// one of them is tuned.
/// </summary>
/// <remarks>
/// Matching runs three fixed tiers, stopping at the first that finds anything: exact on the
/// symbol's bare name, then case-insensitive, then substring. Each returned match carries
/// which tier found it, per §3.4's "each tier of fallback is labelled in the answer".
/// </remarks>
public static class SymbolResolver
{
    /// <param name="pathContains">
    /// When given, every tier's own query carries this as an additional
    /// <c>e.path LIKE '%' || value || '%'</c> term, so LIMIT and tier fallback both see the
    /// already-scoped candidate set — filtering matches after LIMIT/tier selection would both
    /// under-return (an out-of-scope match spends the LIMIT budget) and false-negative (an
    /// out-of-scope exact match blocks the fallback to an in-scope substring match) (D-code-nav
    /// fixup B2). Callers own escaping; this method interpolates the raw value.
    /// </param>
    /// <param name="ceiling">
    /// The last tier this call may fall back to (Phase 3 §6.3). `defined_at` passes nothing —
    /// a human typing a half-remembered name is well served by the substring rung. A
    /// query-time edge join is not: applied per-edge at scale, substring is a fabrication
    /// engine (`join` also matches `joinPath`, `rejoin`, `JoinedTable`), so the caller states
    /// a ceiling rather than a second resolver being written for it (master §9's rule).
    /// </param>
    public static IReadOnlyList<SymbolMatch> Resolve(
        SqliteConnection connection,
        string name,
        int limit,
        string? pathContains = null,
        SymbolMatchTier ceiling = SymbolMatchTier.Substring)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(name) || limit <= 0)
        {
            return [];
        }

        var exact = Query(connection, "e.name = $name", name, limit, pathContains);
        if (exact.Count > 0)
        {
            return Tag(exact, SymbolMatchTier.Exact);
        }

        if (ceiling < SymbolMatchTier.CaseInsensitive)
        {
            return [];
        }

        var caseInsensitive = Query(connection, "e.name = $name COLLATE NOCASE", name, limit, pathContains);
        if (caseInsensitive.Count > 0)
        {
            return Tag(caseInsensitive, SymbolMatchTier.CaseInsensitive);
        }

        if (ceiling < SymbolMatchTier.Substring)
        {
            return [];
        }

        var substring = Query(
            connection, "e.name LIKE '%' || $name || '%' ESCAPE '\\'", LikeEscape(name), limit, pathContains);
        return Tag(substring, SymbolMatchTier.Substring);
    }

    /// <summary>Escapes <c>%</c>, <c>_</c>, and the escape character itself for a LIKE value.</summary>
    public static string LikeEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static List<(string Path, string Name)> Query(
        SqliteConnection connection, string namePredicate, string name, int limit, string? pathContains)
    {
        using var command = connection.CreateCommand();
        var repoClause = pathContains is null ? string.Empty : " AND e.path LIKE '%' || $repo || '%'";
        command.CommandText =
            $"SELECT e.path, e.name FROM entity e WHERE e.kind = 'symbol' AND {namePredicate}{repoClause} "
                + "ORDER BY e.path LIMIT $limit;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$limit", limit);
        if (pathContains is not null)
        {
            command.Parameters.AddWithValue("$repo", pathContains);
        }

        var rows = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private static IReadOnlyList<SymbolMatch> Tag(List<(string Path, string Name)> rows, SymbolMatchTier tier) =>
        rows.Select(row => new SymbolMatch(row.Path, row.Name, tier)).ToList();
}
