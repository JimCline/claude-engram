namespace Engram.Core;

/// <summary>
/// Maps a <see cref="ToolProfile"/> to the MCP tool names it advertises
/// (docs/memory-expansion/03-tool-profiles-spec.md, D-1).
/// </summary>
/// <remarks>
/// <para>The boundary is the exclusion list, not an enumeration (D-1): <c>default</c> is
/// "everything that is not lifecycle," realized here by taking the non-lifecycle tool set as an
/// input rather than deriving it from a hardcoded name list — in production that input is a
/// reflection over <c>EngramMcpTools</c>, which by the codebase's own type boundary (D-5) already
/// contains exactly the non-lifecycle tools. A tool added to that type lands in <c>default</c>
/// with no edit here.</para>
///
/// <para><b><c>full</c> is a concatenation, not a re-derived union.</b> Computing it as
/// <c>(allTools − lifecycle) ∪ lifecycle</c> would make its membership mathematically invariant
/// to <see cref="LifecycleToolNames"/>'s own contents whenever lifecycle stays a subset of the
/// full tool universe — dropping a name from the constant would just move it from the lifecycle
/// bucket into the non-lifecycle one, leaving the total unchanged. Appending
/// <see cref="LifecycleToolNames"/> directly to the given non-lifecycle set means a name dropped
/// from the constant is dropped from <c>full</c>'s count too, which is what makes the constant
/// load-bearing and the spec's tier-1 falsification (removing <c>stop</c>) able to fail.</para>
/// </remarks>
public static class ToolProfiles
{
    /// <summary>The lifecycle tools — exactly <c>EngramServerTools</c>'s three (D-5).</summary>
    public static IReadOnlyList<string> LifecycleToolNames { get; } =
        ["engram_start", "engram_status", "engram_stop"];

    /// <summary>
    /// The tool names advertised under <paramref name="profile"/>, given the non-lifecycle tool
    /// names actually registered (in production, every <c>[McpServerTool]</c> on
    /// <c>EngramMcpTools</c>).
    /// </summary>
    public static IReadOnlyList<string> Resolve(ToolProfile profile, IReadOnlyList<string> nonLifecycleToolNames)
    {
        ArgumentNullException.ThrowIfNull(nonLifecycleToolNames);

        return profile switch
        {
            ToolProfile.Full => [.. nonLifecycleToolNames, .. LifecycleToolNames],
            _ => nonLifecycleToolNames,
        };
    }
}
