using System.ComponentModel;
using System.Reflection;
using System.Text;
using Engram.Cli;
using ModelContextProtocol.Server;

namespace Engram.Integration.Tests;

/// <summary>
/// Pins the text of every MCP tool description.
/// </summary>
/// <remarks>
/// D15 moved the always-true standing guidance out of the session-start primer and into
/// these descriptions, which makes them prompt-engineering surface rather than
/// documentation: they are in the model's context for every session, and a wording change
/// is an interface change that alters behaviour with nothing to catch it. D15 accepted that
/// cost on the condition they get the golden-file treatment D9 gives the recall output
/// contract. This is that.
///
/// A diff here is not a failure to fix by refreshing the golden. It is the signal to ask
/// whether the new wording is better, since the answer decides how often a model reaches
/// for memory at all — which is the entire M0 measurement.
/// </remarks>
public class McpToolDescriptionGoldenTests
{
    private const string GoldenRelativePath = "docs/mcp-tool-descriptions.golden.txt";

    [Fact]
    public void ToolDescriptions_MatchTheGoldenFile()
    {
        var actual = Render();
        var goldenPath = Path.Combine(RepoRoot(), GoldenRelativePath);

        Assert.True(File.Exists(goldenPath), $"golden file missing at {goldenPath}");

        var expected = File.ReadAllText(goldenPath).ReplaceLineEndings("\n");

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            // Written beside the golden rather than over it, so reviewing the change is a
            // diff between two files and refreshing it is a deliberate copy. A test that
            // rewrites its own expectation cannot fail twice.
            var actualPath = goldenPath + ".actual";
            File.WriteAllText(actualPath, actual);
            Assert.Fail($"MCP tool descriptions changed. Compare {GoldenRelativePath} against {GoldenRelativePath}.actual, and update the golden only if the new wording is intended.");
        }
    }

    // The descriptions are the contract; a tool that quietly loses its own is a silent
    // regression the text comparison cannot see, because the rendering would simply be
    // missing a section that nobody remembers should be there.
    [Fact]
    public void EveryMemoryTool_HasANonEmptyDescription()
    {
        var tools = Tools().ToList();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool =>
            Assert.False(
                string.IsNullOrWhiteSpace(tool.GetCustomAttribute<DescriptionAttribute>()?.Description),
                $"{tool.Name} has no description"));
    }

    private static string Render()
    {
        var builder = new StringBuilder();

        foreach (var tool in Tools())
        {
            var name = tool.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? tool.Name;
            builder.Append("tool: ").Append(name).Append('\n');
            builder.Append(tool.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty).Append('\n');

            foreach (var parameter in tool.GetParameters())
            {
                var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (description is null)
                {
                    // Injected services rather than model-visible arguments; they carry no
                    // description precisely because the model never sees them.
                    continue;
                }

                builder.Append("  param: ").Append(parameter.Name).Append('\n');
                builder.Append("  ").Append(description).Append('\n');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    // Ordered by name so the golden does not churn on reflection order, which is not a
    // documented guarantee and would make this test flaky rather than informative.
    private static IEnumerable<MethodInfo> Tools() =>
        typeof(EngramMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? m.Name, StringComparer.Ordinal);

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CLAUDE.md")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException($"could not locate repo root (CLAUDE.md) above {AppContext.BaseDirectory}");
    }
}
