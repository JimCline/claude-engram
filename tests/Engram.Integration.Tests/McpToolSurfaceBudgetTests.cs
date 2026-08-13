using System.ComponentModel;
using System.Reflection;
using Engram.Cli;
using ModelContextProtocol.Server;

namespace Engram.Integration.Tests;

/// <summary>
/// D17: tool definitions are serialized into every session whether or not memory is ever
/// used, so the surface is a budget rather than a free channel. Nothing here judges the
/// prose — only that its cost stays where it was last agreed.
/// </summary>
public class McpToolSurfaceBudgetTests
{
    // Measured 3,964 characters across the seven tools in EngramMcpTools on 2026-08-13, when
    // engram_index_repo joined (spec §6.6a-b) — the situational tools must not dilute
    // recall's pitch, which is the funnel that matters. EngramServerTools's three tools
    // (start/status/stop) are argued as cost in D17 but are not reflected over by
    // ToolMethods() and are not counted in this figure — a separate, unmeasured gap. The
    // +137 headroom is carried over from the previous ceiling's stated purpose of "ordinary
    // wording changes"; raising this number is a deliberate edit that needs a reason in the
    // commit message, not a knob to turn when a description outgrows it.
    private const int MaxDefinitionChars = 4101;

    // 6 -> 7 when engram_index_repo joined: it addresses a filesystem path and spawns a
    // background process, which no existing tool can take as a parameter without changing
    // that tool's addressing model (spec §6.6a-b).
    private const int ExpectedToolCount = 7;

    [Fact]
    public void ToolDefinitions_StayUnderCharacterCeiling()
    {
        var total = 0;
        var breakdown = new List<string>();

        foreach (var method in ToolMethods())
        {
            var description = DescriptionLength(method.GetCustomAttribute<DescriptionAttribute>());
            var parameters = method.GetParameters()
                .Sum(p => DescriptionLength(p.GetCustomAttribute<DescriptionAttribute>()));

            total += description + parameters;
            breakdown.Add($"  {ToolName(method)}: {description} description + {parameters} parameter(s)");
        }

        Assert.True(
            total <= MaxDefinitionChars,
            $"Tool definitions total {total} chars against a ceiling of {MaxDefinitionChars}.\n"
                + string.Join('\n', breakdown)
                + "\nRaise the ceiling only with a rationale, or make the descriptions carry their cost.");
    }

    // A new tool is the most expensive thing that can happen to this budget, so it should
    // not be possible to add one incidentally. D17 requires arguing a new tool's cost
    // against putting a parameter on an existing one; failing here is that argument's cue.
    [Fact]
    public void ToolCount_IsDeliberate()
    {
        var names = ToolMethods().Select(ToolName).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(
            names.Count == ExpectedToolCount,
            $"Expected {ExpectedToolCount} tools, found {names.Count}: {string.Join(", ", names)}");
    }

    // The ceiling only means something if every tool actually declares its cost here.
    [Fact]
    public void EveryTool_HasADescription()
    {
        var undescribed = ToolMethods()
            .Where(m => string.IsNullOrWhiteSpace(m.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Select(ToolName)
            .ToList();

        Assert.True(undescribed.Count == 0, "Tools with no description: " + string.Join(", ", undescribed));
    }

    private static int DescriptionLength(DescriptionAttribute? attribute) =>
        attribute?.Description?.Length ?? 0;

    private static string ToolName(MethodInfo method) =>
        method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(EngramMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
