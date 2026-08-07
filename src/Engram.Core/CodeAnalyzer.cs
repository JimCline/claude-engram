using System.Text;

namespace Engram.Core;

/// <summary>What one analyzed file wants the store to believe: a subject, and one fact about it.</summary>
/// <remarks>
/// Candidates carry no evidence, validity, or provenance — the pipeline supplies those
/// uniformly (observed, regenerable, evidence = path @ blob) so an analyzer cannot get
/// them wrong, and diffing candidates against live facts stays a comparison of
/// (path, predicate, body) triples.
/// </remarks>
public sealed record CodeCandidate(
    string EntityPath,
    string Kind,
    string DisplayName,
    string Predicate,
    string Body);

/// <summary>
/// Tier-0 analysis (D24): managed, in-core, no dependencies, works on any file. Produces
/// extractive candidates only — impressions of prose, declaration lines, import lists.
/// Deeper structure belongs to the tiers that can actually see it.
/// </summary>
public static class CodeAnalyzer
{
    public const int AnalyzerVersion = 1;

    public static IReadOnlyList<CodeCandidate> Analyze(
        string fileEntityPath,
        string content,
        LanguageDefinition language)
    {
        var candidates = new List<CodeCandidate>();
        var fileName = fileEntityPath[(fileEntityPath.LastIndexOf('/') + 1)..];

        if (language.DocHeadings)
        {
            AnalyzeDocument(fileEntityPath, fileName, content, candidates);
            return candidates;
        }

        var impression = language.DeclarationPatterns.Count > 0 || language.ImportPatterns.Count > 0
            ? ImpressionExtractor.FromLeadComment(content)
            : ImpressionExtractor.FromProse(content);

        if (impression is not null)
        {
            candidates.Add(new CodeCandidate(fileEntityPath, "file", fileName, "about", impression));
        }

        AddDeclarations(fileEntityPath, content, language, candidates);
        AddImports(fileEntityPath, fileName, content, language, candidates);

        return candidates;
    }

    private static void AddDeclarations(
        string fileEntityPath,
        string content,
        LanguageDefinition language,
        List<CodeCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pattern in language.DeclarationPatterns)
        {
            foreach (System.Text.RegularExpressions.Match match in LanguageRegistry.Compiled(pattern).Matches(content))
            {
                var name = match.Groups["name"].Value;
                if (name.Length == 0 || !seen.Add(name))
                {
                    continue;
                }

                var line = LineOf(content, match.Index).Trim();
                candidates.Add(new CodeCandidate(
                    CodePaths.ForSymbol(fileEntityPath, name),
                    "symbol",
                    name,
                    "declared-as",
                    Cap(line)));
            }
        }
    }

    private static void AddImports(
        string fileEntityPath,
        string fileName,
        string content,
        LanguageDefinition language,
        List<CodeCandidate> candidates)
    {
        var modules = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var pattern in language.ImportPatterns)
        {
            foreach (System.Text.RegularExpressions.Match match in LanguageRegistry.Compiled(pattern).Matches(content))
            {
                var module = match.Groups["module"].Value;
                if (module.Length > 0)
                {
                    modules.Add(module);
                }
            }
        }

        if (modules.Count > 0)
        {
            // One sorted fact rather than a row per module: reordering imports is not a
            // change of belief, and a single body diffs in one comparison.
            candidates.Add(new CodeCandidate(
                fileEntityPath,
                "file",
                fileName,
                "imports",
                Cap("imports " + string.Join(", ", modules))));
        }
    }

    private static void AnalyzeDocument(
        string fileEntityPath,
        string fileName,
        string content,
        List<CodeCandidate> candidates)
    {
        var lines = content.Split('\n');
        var stack = new List<(int Level, string Slug)>();
        var sections = new List<(string Fragment, string Heading, StringBuilder Body)>();
        var bodies = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var preamble = new StringBuilder();
        var current = preamble;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var level = HeadingLevel(line, out var heading);

            if (level == 0)
            {
                current.AppendLine(line);
                continue;
            }

            while (stack.Count > 0 && stack[^1].Level >= level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack.Add((level, CodePaths.Slug(heading)));

            // Two headings can slug to one fragment; their prose merges under the first,
            // because one address can only hold one section entity.
            var fragment = string.Join('/', stack.ConvertAll(entry => entry.Slug));
            if (!bodies.TryGetValue(fragment, out var body))
            {
                body = new StringBuilder();
                bodies.Add(fragment, body);
                sections.Add((fragment, heading, body));
            }

            current = body;
        }

        var overview = ImpressionExtractor.FromProse(
            preamble.Length > 0 ? preamble.ToString() : StripHeadings(content));
        if (overview is not null)
        {
            candidates.Add(new CodeCandidate(fileEntityPath, "file", fileName, "about", overview));
        }

        foreach (var (fragment, heading, body) in sections)
        {
            var impression = ImpressionExtractor.FromProse(body.ToString());
            if (impression is not null)
            {
                candidates.Add(new CodeCandidate(
                    CodePaths.ForSection(fileEntityPath, fragment),
                    "section",
                    heading,
                    "about",
                    impression));
            }
        }
    }

    private static int HeadingLevel(string line, out string heading)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is 0 or > 6 || hashes == line.Length || line[hashes] != ' ')
        {
            heading = string.Empty;
            return 0;
        }

        heading = line[(hashes + 1)..].Trim().TrimEnd('#').TrimEnd();
        return heading.Length == 0 ? 0 : hashes;
    }

    private static string StripHeadings(string content)
    {
        var builder = new StringBuilder(content.Length);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (HeadingLevel(line, out _) == 0)
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    private static string LineOf(string content, int index)
    {
        var start = content.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = content.IndexOf('\n', index);
        return end < 0 ? content[start..] : content[start..end];
    }

    internal static string Cap(string text)
    {
        if (TokenEstimator.Estimate(text) <= ImpressionExtractor.MaxTokens)
        {
            return text;
        }

        var limit = ImpressionExtractor.MaxTokens * 4;
        var cut = text.LastIndexOf(' ', Math.Min(text.Length, limit) - 1);
        return (cut > 0 ? text[..cut] : text[..limit]).TrimEnd() + "…";
    }
}
