using Engram.Core;

namespace Engram.Core.Tests;

public class CompactionDigestTests
{
    // The summarizer reading this instruction has no tools; naming one would spend its
    // attention on something it structurally cannot do.
    [Fact]
    public void Instruction_NamesNoTool() =>
        Assert.DoesNotContain("engram_", CompactionDigest.Instruction, StringComparison.Ordinal);

    // The harvester (todo 2) has to parse these back out; pin the literal spelling so a
    // change here is deliberate rather than an incidental drift through TagName.
    [Fact]
    public void SentinelsArePinnedToTheirLiteralSpelling()
    {
        Assert.Equal("<engram-digest v=\"1\">", CompactionDigest.OpenSentinel, StringComparer.FromComparison(StringComparison.Ordinal));
        Assert.Equal("</engram-digest>", CompactionDigest.CloseSentinel, StringComparer.FromComparison(StringComparison.Ordinal));
    }
}
