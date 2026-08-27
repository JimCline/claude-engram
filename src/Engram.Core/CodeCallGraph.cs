using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>Why a declaration/caller ranked where it did (Phase 3 spec §6.4) — highest signal wins, never blended.</summary>
public enum CallRankSignal
{
    SameFile,
    QualifierAgreement,
    ImportFilenameMatch,
    SameRepo,
    NameOnly,
}

/// <summary>Whether a file's calls extraction can be trusted as complete (Architect ruling, gap b).</summary>
public enum ExtractionCoverage
{
    /// <summary>The file's language has nothing to extract (tier 0) — a zero-calls answer needs no caveat.</summary>
    NotApplicable,

    /// <summary>Extraction ran under the current grammar/analyzer version — zero calls is the real answer.</summary>
    KnownZero,

    /// <summary>Never extracted, or extracted under a stale version — zero calls may just mean "not looked at yet".</summary>
    Unknown,
}

/// <param name="AttributedToType">
/// True when this call was written inside a member neither tier emits as a symbol — an
/// indexer, an operator, an enum-member initializer, or a local function nested inside one
/// of those (D48's kind exclusions; all-members spec §3) — so the call attributes to the
/// nearest emitted ancestor, the enclosing type, rather than to nothing (Architect ruling,
/// S3). Member visibility no longer decides this: every visibility is emitted. The label is
/// mandatory wherever this is true: an unlabelled type subject reads as "the type calls
/// this", which is false.
/// </param>
public sealed record CallerMatch(
    string CallerPath, CallRankSignal Signal, bool AttributedToType = false, int? AnalyzerTier = null);

public sealed record CalleeMatch(string? DeclarationPath, string Callee, CallRankSignal Signal, int? AnalyzerTier = null);

/// <param name="DistinctSpellings">
/// Every distinct <c>symbol-name</c> spelling <see cref="CodeCallGraph.Callers"/> leaf-matched
/// (§1b): more than one means the query name is ambiguous and the callers below may include
/// unrelated symbols sharing a leaf. Empty when there is nothing to be ambiguous about
/// (<see cref="Found"/> false, or the query resolved but nothing leaf-matched).
/// </param>
public sealed record CallersResult(
    IReadOnlyList<CallerMatch> Callers,
    int DeclarationCount,
    int TotalMatches,
    bool Found,
    ExtractionCoverage Coverage = ExtractionCoverage.Unknown,
    SymbolMatchTier QueryTier = SymbolMatchTier.Exact,
    IReadOnlyList<string> DistinctSpellings = null!)
{
    public IReadOnlyList<string> DistinctSpellings { get; init; } = DistinctSpellings ?? [];
}

public sealed record CalleesResult(
    IReadOnlyList<CalleeMatch> Callees,
    int TotalMatches,
    bool Found,
    ExtractionCoverage Coverage = ExtractionCoverage.Unknown,
    SymbolMatchTier QueryTier = SymbolMatchTier.Exact);

/// <summary>
/// `callers`/`callees` (Phase 3 spec §6): the one ranker (§6.1(iv)/§6.4) shared by both
/// directions, and the leaf-name join (C4) `callers` needs because a `calls` fact's object
/// is the callee as written — `join`, `path.join`, and `os.path.join` are three distinct
/// <c>symbol-name</c> entities that all answer "who calls `join`".
/// </summary>
public static class CodeCallGraph
{
    /// <summary>
    /// No join: every live <c>calls</c> fact whose object leaf-matches <paramref name="query"/>'s
    /// resolved declaration name(s) (§6.2). The result is a superset for an ambiguous name — it is
    /// never narrowed, only labelled and ordered (§6.1(iii)/(iv)).
    /// </summary>
    public static CallersResult Callers(SqliteConnection connection, string query, string? repoNeedle, int limit)
    {
        var declarations = SymbolResolver.Resolve(connection, query, 1000, repoNeedle);
        if (declarations.Count == 0)
        {
            return new CallersResult([], 0, 0, Found: false);
        }

        var leaves = declarations.Select(d => d.Name).Distinct(StringComparer.Ordinal).ToList();
        var declarationFiles = declarations.Select(d => FileOf(d.Path)).Distinct(StringComparer.Ordinal).ToList();

        var objects = MatchingSymbolNames(connection, leaves);
        var distinctSpellings = objects.Select(o => o.Name).Distinct(StringComparer.Ordinal).ToList();
        var calls = LiveCallsToObjects(connection, objects.Select(o => o.Path).ToList(), repoNeedle);

        var ranked = calls
            .Select(c => new CallerMatch(
                c.CallerPath,
                RankFrom(connection, c.CallerPath, declarationFiles),
                IsTypeDeclaration(connection, c.CallerPath),
                c.AnalyzerTier))
            .OrderBy(m => m.Signal)
            .ThenBy(m => m.CallerPath, StringComparer.Ordinal)
            .ToList();

        var coverage = AggregateCoverage(connection, declarationFiles);
        return new CallersResult(
            ranked.Take(limit).ToList(), declarations.Count, ranked.Count, Found: true, coverage, declarations[0].Tier,
            distinctSpellings);
    }

    /// <summary>
    /// The join direction (§6.2): live <c>calls</c> facts whose subject is <paramref name="query"/>'s
    /// own declaration address, each object enriched with its declaration site(s) via the leaf-name
    /// join and ranked by all five §6.4 signals.
    /// </summary>
    public static CalleesResult Callees(SqliteConnection connection, string query, string? repoNeedle, int limit)
    {
        var declarations = SymbolResolver.Resolve(connection, query, 1000, repoNeedle);
        if (declarations.Count == 0)
        {
            return new CalleesResult([], 0, Found: false);
        }

        var coverage = AggregateCoverage(connection, declarations.Select(d => FileOf(d.Path)).Distinct(StringComparer.Ordinal).ToList());
        var calls = LiveCallsFromSubjects(connection, declarations.Select(d => d.Path).ToList(), repoNeedle);

        var results = new List<CalleeMatch>();
        foreach (var (callerPath, callee, analyzerTier) in calls)
        {
            var leaf = CodePaths.LeafOf(callee);
            var qualifier = QualifierOf(callee);
            var candidates = SymbolResolver.Resolve(connection, leaf, 1000, repoNeedle, SymbolMatchTier.CaseInsensitive);

            if (candidates.Count == 0)
            {
                results.Add(new CalleeMatch(null, callee, CallRankSignal.NameOnly, analyzerTier));
                continue;
            }

            foreach (var candidate in candidates)
            {
                var signal = RankFrom(
                    connection, callerPath, [FileOf(candidate.Path)], qualifier, ScopeOfDeclaration(candidate.Path), repoNeedle);
                results.Add(new CalleeMatch(candidate.Path, callee, signal, analyzerTier));
            }
        }

        var ranked = results.OrderBy(r => r.Signal).ThenBy(r => r.Callee, StringComparer.Ordinal).ToList();
        return new CalleesResult(ranked.Take(limit).ToList(), ranked.Count, Found: true, coverage, declarations[0].Tier);
    }

    /// <summary>
    /// One ranker for both directions (§6.1(iv) reuses §6.4): a second implementation that
    /// happens to agree today is the defect the phase spec calls out (acceptance item 15).
    /// <paramref name="qualifier"/> and <paramref name="candidateScope"/> are null for the
    /// `callers` direction, which uses only the first two signals per §6.1(iv).
    /// </summary>
    private static CallRankSignal RankFrom(
        SqliteConnection connection,
        string subjectFilePath,
        IReadOnlyList<string> candidateFiles,
        string? qualifier = null,
        string? candidateScope = null,
        string? repoNeedle = null)
    {
        var subjectFile = FileOf(subjectFilePath);
        if (candidateFiles.Contains(subjectFile, StringComparer.Ordinal))
        {
            return CallRankSignal.SameFile;
        }

        // Exact before approximate (Architect ruling, gap a): a stored qualifier match must
        // outrank the filename heuristic below, or the approximation can displace a real signal.
        if (qualifier is not null && candidateScope == qualifier)
        {
            return CallRankSignal.QualifierAgreement;
        }

        if (candidateFiles.Any(file => ImportFilenameMatch(connection, subjectFile, file)))
        {
            return CallRankSignal.ImportFilenameMatch;
        }

        if (candidateFiles.Any(file => RepoOf(subjectFile) == RepoOf(file)))
        {
            return CallRankSignal.SameRepo;
        }

        return CallRankSignal.NameOnly;
    }

    // Symbol-name entities are shared between "imports" objects and "calls" objects (both
    // written names under /symbol-names/), so the leaf filter runs in C# rather than trying
    // to express §6.5's two-separator rule in SQL.
    private static List<(string Path, string Name)> MatchingSymbolNames(
        SqliteConnection connection, IReadOnlyList<string> leaves)
    {
        var leafSet = new HashSet<string>(leaves, StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.path, e.name FROM entity e WHERE e.kind = 'symbol-name';";

        var matches = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (leafSet.Contains(CodePaths.LeafOf(name)))
            {
                matches.Add((reader.GetString(0), name));
            }
        }

        return matches;
    }

    private static List<(string CallerPath, string Callee, int? AnalyzerTier)> LiveCallsToObjects(
        SqliteConnection connection, IReadOnlyList<string> objectPaths, string? repoNeedle)
    {
        if (objectPaths.Count == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        var placeholders = string.Join(',', objectPaths.Select((_, i) => $"$o{i}"));
        var repoClause = repoNeedle is null ? string.Empty : " AND f.path LIKE '%' || $repo || '%'";
        command.CommandText =
            $"SELECT f.path, o.name, f.analyzer_tier FROM fact f JOIN entity o ON o.id = f.object_id "
                + $"WHERE f.predicate = 'calls' AND f.valid_to IS NULL AND o.path IN ({placeholders}){repoClause};";
        for (var i = 0; i < objectPaths.Count; i++)
        {
            command.Parameters.AddWithValue($"$o{i}", objectPaths[i]);
        }

        if (repoNeedle is not null)
        {
            command.Parameters.AddWithValue("$repo", repoNeedle);
        }

        var results = new List<(string, string, int?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }

        return results;
    }

    private static List<(string CallerPath, string Callee, int? AnalyzerTier)> LiveCallsFromSubjects(
        SqliteConnection connection, IReadOnlyList<string> subjectPaths, string? repoNeedle)
    {
        if (subjectPaths.Count == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        var placeholders = string.Join(',', subjectPaths.Select((_, i) => $"$s{i}"));
        var repoClause = repoNeedle is null ? string.Empty : " AND f.path LIKE '%' || $repo || '%'";
        command.CommandText =
            $"SELECT f.path, o.name, f.analyzer_tier FROM fact f JOIN entity o ON o.id = f.object_id "
                + $"WHERE f.predicate = 'calls' AND f.valid_to IS NULL AND f.path IN ({placeholders}){repoClause};";
        for (var i = 0; i < subjectPaths.Count; i++)
        {
            command.Parameters.AddWithValue($"$s{i}", subjectPaths[i]);
        }

        if (repoNeedle is not null)
        {
            command.Parameters.AddWithValue("$repo", repoNeedle);
        }

        var results = new List<(string, string, int?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }

        return results;
    }

    // Whether `file`'s written imports plausibly reach `target` — approximated by comparing
    // leaves (an import's module name against target's bare filename), because resolving a
    // written module string to a file path is a per-language question (relative segments,
    // package names, TS path aliases) this phase's spec does not define an algorithm for.
    // Named for what it actually checks (a filename match), not for what it stands in for
    // (import consistency) — a reason string is a false explanation otherwise (D30).
    private static bool ImportFilenameMatch(SqliteConnection connection, string file, string target)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT o.name FROM fact f JOIN entity o ON o.id = f.object_id "
                + "WHERE f.path = $file AND f.predicate = 'imports' AND f.valid_to IS NULL;";
        command.Parameters.AddWithValue("$file", file);

        var targetLeaf = System.IO.Path.GetFileNameWithoutExtension(target[(target.LastIndexOf('/') + 1)..]);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(CodePaths.LeafOf(reader.GetString(0)), targetLeaf, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // One resolved query can span several declaration files (an overloaded/ambiguous name);
    // the query as a whole is trustworthy only if every one of them is (Unknown outvotes the
    // rest — reporting "known zero" when even one file was never extracted overclaims).
    private static ExtractionCoverage AggregateCoverage(SqliteConnection connection, IReadOnlyList<string> files)
    {
        var coverages = files.Select(f => CodeIndexer.CoverageOf(connection, f)).ToList();
        if (coverages.Count == 0 || coverages.Any(c => c == ExtractionCoverage.Unknown))
        {
            return ExtractionCoverage.Unknown;
        }

        return coverages.Any(c => c == ExtractionCoverage.KnownZero)
            ? ExtractionCoverage.KnownZero
            : ExtractionCoverage.NotApplicable;
    }

    private static readonly string[] TypeDeclarationKeywords = ["class", "interface", "struct", "record", "enum"];

    // Whether `symbolPath`'s own declared-as fact reads as a type rather than a member —
    // the signal S3's label needs, with no schema change available to carry it directly
    // (§3 forbids touching `entity`). The declaration text is exactly what DeclarationLine
    // wrote in the sidecar, so a type keyword before the first '(' or '{' is reliable: a
    // member's own declaration always carries one of those before any type keyword could
    // appear (a parameter list, an accessor block, or neither for a field/event, which are
    // never type keywords either).
    private static bool IsTypeDeclaration(SqliteConnection connection, string symbolPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM fact WHERE path = $path AND predicate = 'declared-as' AND valid_to IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$path", symbolPath);

        if (command.ExecuteScalar() is not string body)
        {
            return false;
        }

        var head = body[..(body.IndexOfAny(['(', '{']) is var cut && cut >= 0 ? cut : body.Length)];
        return head.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => TypeDeclarationKeywords.Contains(w, StringComparer.Ordinal));
    }

    public static string FileOf(string path)
    {
        var hash = path.IndexOf('#');
        return hash < 0 ? path : path[..hash];
    }

    private static string RepoOf(string path)
    {
        var segments = path.Split('/');
        return segments.Length >= 5 ? string.Join('/', segments[..5]) : path;
    }

    private static string? ScopeOfDeclaration(string path)
    {
        var hash = path.IndexOf('#');
        if (hash < 0)
        {
            return null;
        }

        var fragment = path[(hash + 1)..];
        var slash = fragment.LastIndexOf('/');
        return slash < 0 ? null : fragment[..slash];
    }

    private static string? QualifierOf(string callee)
    {
        var separator = Math.Max(callee.LastIndexOf('.'), callee.LastIndexOf('/'));
        return separator < 0 ? null : callee[..separator];
    }
}
