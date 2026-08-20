using System.Text.RegularExpressions;

namespace Engram.Core;

public enum UserFactKind
{
    /// <summary>Something the user asserted about themselves or their world.</summary>
    PersonalStatement,

    /// <summary>A standing instruction: "always", "never", "from now on", "remember that".</summary>
    Instruction,
}

public sealed record UserStatementCandidate(string Text, UserFactKind Kind);

/// <summary>
/// Picks the sentences of a user's message that are worth remembering.
///
/// The load-bearing observation is that "I went to see a Spiderman movie last Saturday"
/// carries no memory keyword at all — matching on "remember" or "always" would never see
/// it. What identifies it is grammatical: a first-person declarative. That shape is cheap
/// to test for and separates a statement of fact from the two things a prompt is usually
/// made of instead, a question and an instruction.
///
/// Classification runs per sentence, not per message. "I moved to Seattle. Now fix the
/// build." should contribute the first clause and nothing else, and working at sentence
/// granularity means the rest of the message never reaches disk at all.
///
/// This is deliberately a recall-over-precision filter with a bias to silence: it runs on
/// every keystroke-completed message, on the user's own machine, with no model in the
/// loop and no way for the user to see what it decided until afterwards. A missed fact
/// costs one repetition. A wrongly captured one is a sentence the user did not choose to
/// have written down.
/// </summary>
public static partial class UserStatementClassifier
{
    // Short fragments are almost never a durable fact: "I agree", "I see", "my bad".
    private const int MinimumWords = 4;

    // Nothing here is a substitute for the model rewriting the sentence into a
    // self-contained fact. A raw sentence keeps relative time ("last Saturday") that only
    // means something next to the timestamp it was captured with.
    public static IReadOnlyList<UserStatementCandidate> Classify(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return [];
        }

        // A slash command is addressed to the harness, not a thing the user said.
        if (prompt.TrimStart().StartsWith('/'))
        {
            return [];
        }

        var candidates = new List<UserStatementCandidate>();

        // Fenced blocks are stripped before splitting rather than filtered after. A fence
        // sits on its own line, so the splitter would hand the code inside it over as
        // ordinary sentences with the marker left behind on a line of its own — and
        // pasted logs are full of lines that read as first-person prose. Removing the
        // region also means a message can paste a stack trace and still state a fact.
        var prose = CodeFence().Replace(prompt, " ");

        foreach (var raw in SentenceBoundary().Split(prose))
        {
            var sentence = raw.Trim();

            if (WordCount(sentence) < MinimumWords)
            {
                continue;
            }

            if (Directive().IsMatch(sentence))
            {
                candidates.Add(new UserStatementCandidate(sentence, UserFactKind.Instruction));
                continue;
            }

            if (Question().IsMatch(sentence) || AssistantRequest().IsMatch(sentence))
            {
                continue;
            }

            if (FirstPersonDeclarative().IsMatch(sentence) && !FirstPersonIntent().IsMatch(sentence))
            {
                candidates.Add(new UserStatementCandidate(sentence, UserFactKind.PersonalStatement));
            }
        }

        return candidates;
    }

    private static int WordCount(string sentence)
    {
        var count = 0;
        var inWord = false;

        foreach (var c in sentence)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceBoundary();

    // The trailing alternative catches an unterminated fence, which is what a truncated
    // paste looks like — otherwise everything after it would be treated as prose.
    [GeneratedRegex(@"```[\s\S]*?```|```[\s\S]*$")]
    private static partial Regex CodeFence();

    // Only the unambiguous standing-instruction openers. Imperatives like "stop x" and
    // "prefer y" were tried and dropped: "stop the server" is a request for right now,
    // indistinguishable by shape from "stop using tabs", and guessing wrong stores a
    // one-off command as a permanent rule.
    [GeneratedRegex(
        @"^\s*(remember|note that|fyi|for the record|for future reference)\b"
            + @"|\b(always|never|from now on|going forward|from here on)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Directive();

    [GeneratedRegex(
        @"\?\s*$|^\s*(who|what|when|where|why|how|which|is|are|was|were|do|does|did|can|could|should|would|will|has|have|am)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Question();

    // Only the last two alternatives ever decide anything. A sentence opening with
    // "please" or "show me" is not first-person, so the person gate below rejects it
    // regardless; those openers are kept as a cheap explicit statement of intent, not
    // because removing them would change a result. The alternatives that carry weight are
    // the ones an instruction can share with a statement of fact — "I need you to…" and
    // "I think you should…" both open exactly like something worth remembering.
    [GeneratedRegex(
        @"^\s*(please|let'?s|show me|give me|tell me|help me|make it|go ahead)\b"
            // The contraction has no space before it — "I'd like you to" is i + 'd, not
            // i + whitespace + 'd — so the apostrophe form needs its own alternative.
            + @"|\bi(\s+need|\s+want|\s+would\s+like|'?d\s+like)\s+you\s+to\b"
            + @"|\byou\s+(should|need to|must|have to)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AssistantRequest();

    [GeneratedRegex(@"^\s*(i|i'?m|i'?ve|my|mine|we|we'?re|we'?ve|our)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FirstPersonDeclarative();

    // "I'll take a look", "I'm going to rerun it" — intent about the task at hand, stale
    // the moment it is acted on.
    [GeneratedRegex(@"^\s*(i'?ll\b|i\s+will\b|i'?m\s+(going\s+to|about\s+to)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FirstPersonIntent();
}
