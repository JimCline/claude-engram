namespace Engram.Core;

/// <summary>
/// The predicates whose facts carry an <c>object_id</c>. A predicate is either always
/// object-bearing or never — the two partial unique indexes of schema v13 do not compose
/// otherwise, and an objectless and an object-bearing live fact would coexist on one
/// subject+predicate with both returned.
/// </summary>
public static class CodePredicates
{
    public static readonly IReadOnlySet<string> EdgeBearing =
        new HashSet<string>(StringComparer.Ordinal) { "imports" };   // Phase 3 adds "calls"

    /// <summary>
    /// <see cref="EdgeBearing"/> as a quoted, comma-joined SQL list — sorted ordinally so it
    /// matches <c>docs/engram-schema.sql</c>'s literal byte-for-byte regardless of the
    /// backing <see cref="HashSet{T}"/>'s enumeration order, which is what
    /// <c>AMigratedStore_HasTheSameLexicalIndexAsAFreshOne</c> compares.
    /// </summary>
    public static readonly string EdgeBearingSqlList = string.Join(
        ", ",
        EdgeBearing.OrderBy(p => p, StringComparer.Ordinal).Select(p => "'" + p.Replace("'", "''") + "'"));
}
