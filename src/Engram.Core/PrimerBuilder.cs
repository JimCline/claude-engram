namespace Engram.Core;

public static class PrimerBuilder
{
    public const int MaxTokens = 300;

    private const string Instruction =
        "Engram memory is available and cheap. Call engram_recall before exploring files; " +
        "use engram_remember for durable facts you learn; flush learnings via engram_digest " +
        "before the session ends.";

    public static string Build(IReadOnlyList<CannedFact> facts)
    {
        var lines = new List<string> { Instruction };
        var tokens = TokenEstimator.Estimate(Instruction);

        AppendSection(lines, ref tokens, "User:", facts.Where(f => f.Scope == "user").Take(5));
        AppendSection(lines, ref tokens, "Project:", facts.Where(f => f.Scope == "project").Take(5));

        return string.Join('\n', lines);
    }

    private static void AppendSection(List<string> lines, ref int tokens, string header, IEnumerable<CannedFact> facts)
    {
        var factLines = facts.Select(f => $"- {f.Body}").ToList();
        if (factLines.Count == 0)
        {
            return;
        }

        var headerTokens = TokenEstimator.Estimate(header);
        if (tokens + headerTokens > MaxTokens)
        {
            return;
        }

        lines.Add(header);
        tokens += headerTokens;

        foreach (var line in factLines)
        {
            var lineTokens = TokenEstimator.Estimate(line);
            if (tokens + lineTokens > MaxTokens)
            {
                break;
            }

            lines.Add(line);
            tokens += lineTokens;
        }
    }
}
