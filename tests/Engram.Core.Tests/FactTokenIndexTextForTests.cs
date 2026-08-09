using Engram.Core;

namespace Engram.Core.Tests;

/// <summary>
/// <c>FactTokenIndex.TextFor</c> in isolation, with no database involved: what a fact
/// contributes to the overlap index, split by scope.
/// </summary>
/// <remarks>
/// The session branch mirrors <c>SessionFacts.ToSessionFact</c>'s own rule for when a note's
/// subject is real rather than its fingerprint default — see
/// <c>SessionFactsTests</c> for that rule exercised through the store.
/// </remarks>
public class FactTokenIndexTextForTests
{
    [Fact]
    public void LongTermFact_IncludesTheSubjectNameUnconditionally()
    {
        var text = FactTokenIndex.TextFor("/knowledge/testing/kestrel", "kestrel", "It binds loopback only.");

        Assert.Contains("kestrel", Tokenizer.Tokenize(text));
        Assert.Contains("loopback", Tokenizer.Tokenize(text));
    }

    /// <summary>
    /// A long-term entity's name equals its path leaf in the ordinary case — <c>EnsureEntity</c>
    /// names it that way by default — and the ranker has always tokenized it regardless
    /// (<c>CannedFact.Subject</c> is unconditional). Only the session branch excludes a name
    /// that matches the leaf.
    /// </summary>
    [Fact]
    public void LongTermFact_IncludesTheNameEvenWhenItEqualsThePathLeaf()
    {
        var text = FactTokenIndex.TextFor("/knowledge/hooks", "hooks", "Session start fires on resume.");

        Assert.Contains("hooks", Tokenizer.Tokenize(text));
    }

    [Fact]
    public void SessionFact_WithNoCustomSubject_ExcludesTheFingerprintLeaf()
    {
        var text = FactTokenIndex.TextFor(
            "/sessions/5/abcd1234", "abcd1234", "the deploy pipeline uses github actions");

        Assert.DoesNotContain("abcd1234", Tokenizer.Tokenize(text));
        Assert.Contains("pipeline", Tokenizer.Tokenize(text));
    }

    [Fact]
    public void SessionFact_UnderAnAgentPath_ExcludesTheFingerprintLeaf()
    {
        var text = FactTokenIndex.TextFor(
            "/sessions/5/task-gopher/abcd1234", "abcd1234", "drains the queue before compaction");

        Assert.DoesNotContain("abcd1234", Tokenizer.Tokenize(text));
        Assert.Contains("drains", Tokenizer.Tokenize(text));
    }

    [Fact]
    public void SessionFact_WithACustomSubject_IncludesIt()
    {
        var text = FactTokenIndex.TextFor(
            "/sessions/5/abcd1234", "keyboard layout", "prefers dvorak over qwerty for typing");

        Assert.Contains("keyboard", Tokenizer.Tokenize(text));
        Assert.Contains("layout", Tokenizer.Tokenize(text));
        Assert.Contains("dvorak", Tokenizer.Tokenize(text));
    }
}
