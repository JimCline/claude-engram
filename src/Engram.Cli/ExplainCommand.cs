using System.Globalization;
using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// Shows why recall returned what it returned, and what it left behind (D21).
/// </summary>
/// <remarks>
/// The health metric is recall coverage, which makes a missed recall the unit of debugging — and
/// a missed recall has four distinct causes that every other output renders identically. The
/// query terms were filtered away; nothing matched; something matched and ranked too low; or it
/// matched, ranked fine, and the token budget cut it. Only the last is common, and only this
/// separates them.
/// </remarks>
internal static class ExplainCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        int? budget = null;
        var limit = 20;
        string? session = null;
        var terms = new List<string>();

        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--budget" when i + 1 < rest.Length:
                    if (!int.TryParse(rest[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var given) || given <= 0)
                    {
                        stderr.WriteLine("error: --budget takes a positive number of tokens");
                        return 1;
                    }

                    budget = given;
                    break;

                case "--limit" when i + 1 < rest.Length:
                    if (!int.TryParse(rest[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit <= 0)
                    {
                        stderr.WriteLine("error: --limit takes a positive number of candidates");
                        return 1;
                    }

                    break;

                case "--session" when i + 1 < rest.Length:
                    session = rest[++i];
                    break;

                default:
                    if (rest[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        stderr.WriteLine($"error: unexpected argument '{rest[i]}'");
                        return 1;
                    }

                    terms.Add(rest[i]);
                    break;
            }
        }

        var query = string.Join(' ', terms);
        if (query.Length == 0)
        {
            stderr.WriteLine("error: explain needs a query, e.g. engram explain \"sqlite pragmas\"");
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        // Defaulted from config, not from a constant, so explain reports the budget recall would
        // actually spend on this instance rather than the one it ships with.
        var configured = RetrievalSettings.Read(ConfigFile.Load(home.ConfigPath));
        foreach (var problem in configured.Problems)
        {
            stderr.WriteLine($"warning: {problem}");
        }

        using var connection = EngramDatabase.OpenInitialized(home);

        // explain is the one command that should pay to find out. Under provider = "local" this
        // starts a model, embeds the query through it, and stops it again — seconds, and the
        // whole point of the command is answering whether the vector lane really works rather
        // than reporting that this process is not the server.
        using var local = new LocalRuntime(home);

        var explanation = RetrievalExplainer.Explain(
            connection,
            home,
            query,
            budget ?? configured.BudgetTokens,
            limit,
            session,
            DateTimeOffset.UtcNow,
            Environment.GetEnvironmentVariable,
            local);

        Write(explanation, limit, stdout);
        return 0;
    }

    private static void Write(RetrievalExplanation explanation, int limit, TextWriter stdout)
    {
        var recall = explanation.Recall;

        stdout.WriteLine($"EXPLAIN \"{recall.Query}\"");
        stdout.WriteLine();

        stdout.WriteLine(recall.QueryTerms.Count > 0
            ? $"terms   {string.Join(", ", recall.QueryTerms)}"
            : "terms   none — the query held no searchable tokens");

        if (recall.DroppedTerms.Count > 0)
        {
            // A term the ranker discarded is a match the user is still expecting.
            stdout.WriteLine($"dropped {string.Join(", ", recall.DroppedTerms)}  (stopword, or under three characters)");
        }

        stdout.WriteLine();
        WriteCandidates(explanation, limit, stdout);
        WriteMissed(explanation, stdout);

        stdout.WriteLine();
        stdout.WriteLine("lanes");
        var width = explanation.Lanes.Max(l => l.Name.Length);
        foreach (var lane in explanation.Lanes)
        {
            stdout.WriteLine($"  {lane.Name.PadRight(width)}  {Describe(lane.State)}  {lane.Detail}");
        }

        stdout.WriteLine();
        stdout.WriteLine(
            $"budget  {recall.TokensUsed}/{recall.BudgetTokens} tokens · "
            + $"{explanation.PackedCount} of {recall.Candidates.Count} candidates returned · "
            + $"coverage {RecallEngine.ToText(recall.Coverage)}");
    }

    private static void WriteCandidates(RetrievalExplanation explanation, int limit, TextWriter stdout)
    {
        if (explanation.Candidates.Count == 0)
        {
            stdout.WriteLine("no fact scored on any query term.");
            return;
        }

        stdout.WriteLine("  #  handle     rrf     ovl  fts        vec        sal   tier      tok  in  fact");

        var rank = 0;
        foreach (var explained in explanation.Candidates)
        {
            rank++;
            if (rank > limit)
            {
                stdout.WriteLine($"  … {explanation.Candidates.Count - limit} more (--limit to see them)");
                break;
            }

            var candidate = explained.Candidate;
            stdout.WriteLine(string.Join(
                "  ",
                rank.ToString(CultureInfo.InvariantCulture).PadLeft(3),
                candidate.Handle.PadRight(8),
                candidate.Fused.ToString("0.0000", CultureInfo.InvariantCulture),
                Position(candidate.OverlapRank).PadLeft(3),
                Lexical(explained.Lexical, candidate.LexicalRank).PadRight(9),
                Vector(explained.Vector).PadRight(9),
                Salience(explained.Salience).PadRight(4),
                (explained.Tier ?? Origin(candidate.Origin)).PadRight(8),
                candidate.Tokens.ToString(CultureInfo.InvariantCulture).PadLeft(3),
                candidate.Packed ? "yes" : "no ",
                Trim(candidate.Line)));
        }

        // Which lane carried a fact is the question this whole command exists to answer, so it
        // gets a line rather than leaving the reader to scan two columns for dashes.
        var overlapOnly = explanation.Candidates.Count(c => c.Candidate.LexicalRank is null);
        var lexicalOnly = explanation.Candidates.Count(c => c.Candidate.OverlapRank is null);
        if (overlapOnly > 0 || lexicalOnly > 0)
        {
            stdout.WriteLine(
                $"  ({overlapOnly} found only by term overlap, {lexicalOnly} only by fts5 — "
                + $"{explanation.Candidates.Count - overlapOnly - lexicalOnly} by both)");
        }
    }

    private static void WriteMissed(RetrievalExplanation explanation, TextWriter stdout)
    {
        if (explanation.MissedCount == 0)
        {
            return;
        }

        stdout.WriteLine();
        stdout.WriteLine($"{explanation.MissedCount.ToString(CultureInfo.InvariantCulture)} more candidate(s) matched, below the display limit");
    }

    private static string Position(int? rank) =>
        rank is { } r ? "#" + r.ToString(CultureInfo.InvariantCulture) : "—";

    private static string Lexical(LexicalHit? hit, int? rank) => hit is null || rank is null
        ? "—"
        : $"#{rank} {hit.Bm25.ToString("0.0", CultureInfo.InvariantCulture)}";

    private static string Vector(VectorHit? hit) => hit is null
        ? "—"
        : $"#{hit.Rank} {hit.Distance.ToString("0.00", CultureInfo.InvariantCulture)}";

    private static string Salience(double? score) =>
        score?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—";

    // A session note has no learned_via to show here; naming the tier it came from is the
    // honest substitute, and it is what a reader wants at that column anyway.
    private static string Origin(FactOrigin origin) => origin switch
    {
        FactOrigin.CurrentSession => "session",
        FactOrigin.PriorSession => "prior",
        _ => "?",
    };

    private static string Describe(LaneState state) => state switch
    {
        LaneState.Ranking => "RANKING    ",
        LaneState.Contributing => "contributes",
        LaneState.Idle => "idle       ",
        LaneState.Off => "off        ",
        LaneState.Unavailable => "unavailable",
        _ => "not built  ",
    };

    private static string Trim(string line)
    {
        var single = line.ReplaceLineEndings(" ");
        return single.Length <= 72 ? single : single[..71] + "…";
    }
}
