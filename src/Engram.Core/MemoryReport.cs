using System.Text;
using Microsoft.Data.Sqlite;

namespace Engram.Core;

/// <summary>
/// The rendered document, and the counts its telemetry event carries (D22 §3.6 rule 4 — computed
/// once, from the same materialized set the document itself was built from, so the log can never
/// disagree with the body).
/// </summary>
public sealed record MemoryReportResult(
    string Document,
    int Total,
    int Live,
    int Closed,
    int ExcludedRegenerable);

/// <summary>
/// Renders D22's Markdown report of every fact the store holds, including closed and superseded
/// ones, with nothing truncated.
/// </summary>
public static class MemoryReport
{
    public static MemoryReportResult Render(
        SqliteConnection connection,
        string databasePath,
        int schemaVersion,
        bool authoredOnly,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(databasePath);
        ArgumentNullException.ThrowIfNull(zone);

        // FactJournal.Read is the one query that already returns closed facts with no LIMIT
        // (D22 §4.1) — report is a second caller of it, not a second copy of the SQL.
        var facts = FactJournal.Read(connection).ToList();

        var total = facts.Count;
        var live = facts.Count(f => f.ValidTo is null);
        var closed = total - live;
        var excludedRegenerable = authoredOnly ? facts.Count(f => f.Regenerable) : 0;

        var scoped = authoredOnly ? facts.Where(f => !f.Regenerable) : facts;

        var ordered = scoped
            .OrderBy(f => f.Subject, StringComparer.Ordinal)
            .ThenBy(f => f.Predicate, StringComparer.Ordinal)
            .ThenBy(f => f.ValidFrom)
            .ThenBy(f => f.Id)
            .ToList();

        var sb = new StringBuilder();
        WriteHeader(sb, databasePath, schemaVersion, total, live, closed, authoredOnly, excludedRegenerable, now, zone);

        sb.AppendLine("## Authored facts");
        sb.AppendLine();
        WriteBody(sb, ordered.Where(f => !f.Regenerable).ToList(), zone);

        sb.AppendLine("## Derived facts (regenerable)");
        sb.AppendLine();
        WriteBody(sb, ordered.Where(f => f.Regenerable).ToList(), zone);

        return new MemoryReportResult(sb.ToString(), total, live, closed, excludedRegenerable);
    }

    private static void WriteHeader(
        StringBuilder sb,
        string databasePath,
        int schemaVersion,
        int total,
        int live,
        int closed,
        bool authoredOnly,
        int excludedRegenerable,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        sb.AppendLine("# Engram memory report");
        sb.AppendLine();
        sb.AppendLine($"generated: {MomentText.In(now.ToUnixTimeSeconds(), zone)}");
        sb.AppendLine($"store: {databasePath} (schema {schemaVersion})");
        sb.AppendLine($"facts: {total} total — {live} live, {closed} closed");
        sb.AppendLine(authoredOnly
            ? $"scope: authored facts only — {excludedRegenerable} regenerable fact(s) excluded"
            : "scope: all facts");
        sb.AppendLine();
    }

    private static void WriteBody(StringBuilder sb, IReadOnlyList<JournalFact> facts, TimeZoneInfo zone)
    {
        if (facts.Count == 0)
        {
            sb.AppendLine("none");
            sb.AppendLine();
            return;
        }

        string? currentSubject = null;
        string? currentPredicate = null;

        foreach (var fact in facts)
        {
            if (!string.Equals(fact.Subject, currentSubject, StringComparison.Ordinal))
            {
                currentSubject = fact.Subject;
                currentPredicate = null;
                sb.AppendLine($"### {currentSubject}");
                sb.AppendLine();
            }

            if (!string.Equals(fact.Predicate, currentPredicate, StringComparison.Ordinal))
            {
                currentPredicate = fact.Predicate;
                sb.AppendLine($"#### {currentPredicate}");
                sb.AppendLine();
            }

            WriteEntry(sb, fact, zone);
        }
    }

    private static void WriteEntry(StringBuilder sb, JournalFact fact, TimeZoneInfo zone)
    {
        var line = new StringBuilder(fact.ValidTo is null ? "- **live**" : "- **closed**");
        line.Append(" · from ").Append(MomentText.In(fact.ValidFrom, zone));

        if (fact.ValidTo is { } validTo)
        {
            line.Append(" · closed ").Append(MomentText.In(validTo, zone));

            if (fact.SupersededBy is { } supersededBy)
            {
                line.Append(" · superseded by #").Append(supersededBy);
            }

            if (fact.SupersessionReason is { Length: > 0 } reason)
            {
                line.Append(" · reason: ").Append(reason);
            }
        }

        sb.AppendLine(line.ToString());
        AppendFenced(sb, fact.Body);

        var metadata = MetadataLine(fact);
        if (metadata is not null)
        {
            sb.AppendLine(metadata);
        }

        if (fact.Details is { Length: > 0 } details)
        {
            sb.AppendLine("details:");
            AppendFenced(sb, details);
        }

        sb.AppendLine();
    }

    private static string? MetadataLine(JournalFact fact)
    {
        var parts = new List<string>();

        if (fact.Object is { Length: > 0 } obj)
        {
            parts.Add(fact.ObjectKind is { Length: > 0 } objectKind
                ? $"object: {obj} ({objectKind})"
                : $"object: {obj}");
        }

        parts.Add($"scope: {fact.Scope}");
        parts.Add($"learned via: {fact.LearnedVia}");

        if (fact.Evidence is { Length: > 0 } evidence)
        {
            parts.Add($"evidence: {evidence}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Fences <paramref name="content"/> at <c>max(3, longest run of backticks + 1)</c> — the
    /// CommonMark rule that a fence closes only on a run at least its own length — so a body
    /// containing its own triple-backtick fence, or a leading <c>#</c>/table pipe, cannot forge a
    /// heading or break out into the rest of the document (D22 §5.5). Preserves the content
    /// exactly: no escaping, no re-indentation, embedded newlines and leading whitespace intact.
    /// </summary>
    private static void AppendFenced(StringBuilder sb, string content)
    {
        var longestRun = 0;
        var currentRun = 0;
        foreach (var ch in content)
        {
            if (ch == '`')
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        var fence = new string('`', Math.Max(3, longestRun + 1));

        sb.AppendLine(fence);
        sb.Append(content);
        if (content.Length == 0 || content[^1] != '\n')
        {
            sb.Append('\n');
        }

        sb.AppendLine(fence);
    }
}
