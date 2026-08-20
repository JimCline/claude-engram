using Engram.Core;

namespace Engram.Core.Tests;

// docs/memory-expansion/03-tool-profiles-spec.md, "Tests by tier (D9)".
public class ToolProfilesTests
{
    private static readonly IReadOnlyList<string> EightDefaultTools =
    [
        "engram_recall", "engram_remember", "engram_forget", "engram_revise",
        "engram_expand", "engram_browse", "engram_judge", "engram_index_repo",
    ];

    [Fact]
    public void Default_IsExactlyTheGivenNonLifecycleTools()
    {
        var resolved = ToolProfiles.Resolve(ToolProfile.Default, EightDefaultTools);

        Assert.Equal(EightDefaultTools, resolved);
    }

    // Falsify (per the spec): temporarily remove "engram_stop" from
    // ToolProfiles.LifecycleToolNames without touching this test — Full then drops to 10 and
    // this assertion reddens. Concatenating LifecycleToolNames directly onto the given
    // non-lifecycle set (rather than re-deriving it as a union) is what makes this possible: a
    // union of (allTools - lifecycle) with lifecycle is invariant to lifecycle's own contents
    // whenever lifecycle stays a subset of allTools, which would make this falsification
    // impossible to satisfy under that shape.
    [Fact]
    public void Full_ContainsAllElevenTools()
    {
        var resolved = ToolProfiles.Resolve(ToolProfile.Full, EightDefaultTools);

        Assert.Equal(11, resolved.Count);
        Assert.Equal(
            [.. EightDefaultTools, "engram_start", "engram_status", "engram_stop"],
            resolved);
    }

    // D-1: the mapping is derived from the exclusion list, not a literal. Falsify: hardcode
    // Default to an eight-name literal in ToolProfiles.Resolve and this test still passes
    // wrongly — the property this guards is that a ninth input name reaches the output with no
    // mapping edit at all, which only holds because Resolve treats the input as data.
    [Fact]
    public void Default_IncludesANinthToolAddedToTheInput_WithNoMappingEdit()
    {
        var nineTools = (IReadOnlyList<string>)[.. EightDefaultTools, "engram_something_new"];

        var resolved = ToolProfiles.Resolve(ToolProfile.Default, nineTools);

        Assert.Contains("engram_something_new", resolved);
        Assert.Equal(9, resolved.Count);
    }

    [Fact]
    public void LifecycleToolNames_IsExactlyTheThreeServerTools()
    {
        Assert.Equal(["engram_start", "engram_status", "engram_stop"], ToolProfiles.LifecycleToolNames);
    }
}
