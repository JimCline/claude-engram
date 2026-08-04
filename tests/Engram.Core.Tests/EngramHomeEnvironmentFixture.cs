[assembly: AssemblyFixture(typeof(Engram.Core.Tests.EngramHomeEnvironmentFixture))]

namespace Engram.Core.Tests;

public sealed class EngramHomeEnvironmentFixture : IDisposable
{
    private readonly string? _previousEngramHome;

    public EngramHomeEnvironmentFixture()
    {
        _previousEngramHome = Environment.GetEnvironmentVariable("ENGRAM_HOME");
        var tempDirectory = Path.Combine(Path.GetTempPath(), "engram-home-fixture-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ENGRAM_HOME", tempDirectory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ENGRAM_HOME", _previousEngramHome);
    }
}
