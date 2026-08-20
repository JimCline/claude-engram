using System.ComponentModel;
using System.Reflection;
using Engram.Cli;
using Engram.Core;
using ModelContextProtocol.Server;

namespace Engram.Integration.Tests;

/// <summary>D-4, first class: excluding a tool must not leave a dangling name in a shipped
/// description belonging to a tool that is still advertised.</summary>
public class ToolProfileReferenceIntegrityTests
{
    [Theory]
    [InlineData(ToolProfile.Default)]
    [InlineData(ToolProfile.Full)]
    public void NoShippedDescription_NamesAToolExcludedFromItsOwnProfile(ToolProfile profile)
    {
        var allTools = AllTools().ToList();
        var includedTypes = ServeCommand.ToolTypesFor(profile).ToHashSet();
        var includedNames = allTools
            .Where(t => includedTypes.Contains(t.Type))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        var excludedNames = allTools.Select(t => t.Name).Where(n => !includedNames.Contains(n));

        var shippedText = string.Join('\n', allTools
            .Where(t => includedTypes.Contains(t.Type))
            .SelectMany(t => Descriptions(t.Method)));

        foreach (var excludedName in excludedNames)
        {
            Assert.DoesNotContain(excludedName, shippedText, StringComparison.Ordinal);
        }
    }

    // D-4's own falsification: exclude engram_judge from `default` and confirm the general
    // property reddens — not by asserting the known start/status/stop pairs, which would pass
    // with the general property broken. engram_remember's shipped description names
    // engram_judge (D-1), so simulating judge's exclusion here reproduces exactly the shape of
    // defect the mechanism must catch.
    [Fact]
    public void GeneralProperty_CatchesAHardDanglingName_NotJustTheKnownLifecyclePairs()
    {
        var allTools = AllTools().ToList();
        var simulatedIncluded = allTools.Where(t => t.Name != "engram_judge");

        var shippedText = string.Join('\n', simulatedIncluded.SelectMany(t => Descriptions(t.Method)));

        Assert.Contains("engram_judge", shippedText, StringComparison.Ordinal);
    }

    private static IEnumerable<(Type Type, string Name, MethodInfo Method)> AllTools() =>
        new[] { typeof(EngramMcpTools), typeof(EngramServerTools) }
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(m => (type, m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name, m)));

    private static IEnumerable<string> Descriptions(MethodInfo method)
    {
        if (method.GetCustomAttribute<DescriptionAttribute>()?.Description is { } description)
        {
            yield return description;
        }

        foreach (var parameter in method.GetParameters())
        {
            if (parameter.GetCustomAttribute<DescriptionAttribute>()?.Description is { } paramDescription)
            {
                yield return paramDescription;
            }
        }
    }
}
