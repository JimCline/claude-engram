using System.Text.Json;

namespace Engram.EndToEnd.Tests;

public class StatusCommandTests
{
    [Fact]
    public void StatusJson_AgainstAHomeWithNoServer_ExitsOneAndParsesCleanly()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "status", "--json");

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("NotRunning", root.GetProperty("Server").GetString());
        Assert.Equal(home.Root, root.GetProperty("Home").GetString());
        Assert.False(root.TryGetProperty("Pid", out _));
    }
}
