using Engram.Core;

namespace Engram.Integration.Tests;

public class EngramHomeEnvironmentFixtureTests
{
    [Fact]
    public void ResolveFromProcess_DoesNotResolveToTheRealUserHome()
    {
        var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var realHome = Path.GetFullPath(Path.Combine(userProfileDirectory, ".engram"));

        var home = EngramHome.ResolveFromProcess(null);

        Assert.NotEqual(realHome, home.Root, StringComparer.OrdinalIgnoreCase);
    }
}
