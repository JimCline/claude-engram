using System.Reflection;
using Engram.Cli;
using Engram.Core;
using ModelContextProtocol.Server;

namespace Engram.Integration.Tests;

/// <remarks>
/// No in-process MCP transport harness exists in this project — only
/// <c>Engram.EndToEnd.Tests</c> drives a live connection, against the published binary via
/// <c>HttpMcpClient</c>. This tests the registration decision <see cref="ServeCommand"/> itself
/// makes — which tool types it hands to <c>AddMcpServer().WithTools&lt;T&gt;()</c> — via the
/// internal <see cref="ServeCommand.ToolTypesFor"/>, counting each type's
/// <c>[McpServerTool]</c>-attributed methods the same way
/// <c>McpToolDescriptionGoldenTests</c> does. The tier-3 test drives the live connection.
/// </remarks>
public class ToolProfileRegistrationTests
{
    [Fact]
    public void Default_RegistersExactlyEightTools()
    {
        Assert.Equal(8, ToolCountFor(ToolProfile.Default));
    }

    [Fact]
    public void Full_RegistersExactlyElevenTools()
    {
        Assert.Equal(11, ToolCountFor(ToolProfile.Full));
    }

    // Falsify: hardcode ToolTypesFor to always return both types regardless of profile, confirm
    // Default_RegistersExactlyEightTools reddens.
    private static int ToolCountFor(ToolProfile profile) =>
        ServeCommand.ToolTypesFor(profile)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Count(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
