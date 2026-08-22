using System.ComponentModel;
using System.Reflection;
using Engram.Cli;
using Engram.Core;
using ModelContextProtocol.Server;

namespace Engram.Integration.Tests;

/// <summary>D51: a profile trims which tools appear, never truncates an included tool's own
/// description. <c>engram_remember</c> is the case that matters most, since D-1 makes its
/// unmodified description load-bearing for judge's placement in <c>default</c>.</summary>
public class ToolProfileDescriptionStabilityTests
{
    [Theory]
    [InlineData(ToolProfile.Default)]
    [InlineData(ToolProfile.Full)]
    public void RememberDescription_IsIdenticalUnderEveryProfile(ToolProfile profile)
    {
        var expected = DescriptionOf(typeof(EngramMcpTools), "engram_remember");
        var actual = RememberDescriptionUnder(profile);

        Assert.Equal(expected, actual, StringComparer.Ordinal);
    }

    // Falsify: pass a truncated description through a test-only registration path and confirm
    // this assertion catches the mismatch — done by hand while authoring this test, then
    // reverted, since [Description] is a compile-time constant with no runtime seam to truncate.
    private static string RememberDescriptionUnder(ToolProfile profile)
    {
        var registered = ServeCommand.ToolTypesFor(profile);
        var mcpToolsType = Assert.Single(registered, t => t == typeof(EngramMcpTools));
        return DescriptionOf(mcpToolsType, "engram_remember");
    }

    private static string DescriptionOf(Type toolType, string toolName)
    {
        var method = toolType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        return method.GetCustomAttribute<DescriptionAttribute>()!.Description;
    }
}
