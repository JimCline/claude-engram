namespace Engram.EndToEnd.Tests;

/// <summary>
/// <c>engram compact</c> wiring: routing, the argument surface, and the refusal paths.
/// What pruning actually does is proven at the integration tier; these hold the part only
/// the published binary can prove — that the command exists and refuses correctly.
/// </summary>
public class CompactCommandTests
{
    [Fact]
    public void Compact_WithoutAStore_RefusesToCreateOne()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome(initialize: false);

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "compact");

        Assert.Equal(1, exitCode);
        Assert.Contains("no store", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_OnAFreshStore_DefaultsToADryRunThatFindsNothing()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "compact");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Dry run only", stdout, StringComparison.Ordinal);
        Assert.Contains("nothing to prune", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_WithAnUnrootedPath_IsRefused()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "compact", "--path", "src/Auth.cs");

        Assert.Equal(1, exitCode);
        Assert.Contains("rooted memory path", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_WithAnUnknownArgument_ExitsOne()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, _, stderr) = EngramProcess.Run(home.Root, "compact", "--prune");

        Assert.Equal(1, exitCode);
        Assert.Contains("unexpected argument", stderr, StringComparison.Ordinal);
    }
}
