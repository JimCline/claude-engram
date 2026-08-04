using Engram.Cli;

namespace Engram.Integration.Tests;

public class InitCommandTests
{
    [Fact]
    public void Init_CreatesAllFourDirectoriesAndConfig()
    {
        using var sandbox = new SandboxHome();
        var home = sandbox.Home;
        Directory.Delete(home.Root, recursive: true);

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliApp.Run(["--home", home.Root, "init"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(Directory.Exists(home.Root));
        Assert.True(Directory.Exists(home.ModelsDir));
        Assert.True(Directory.Exists(home.QueueDir));
        Assert.True(Directory.Exists(home.ReportDir));
        Assert.True(File.Exists(home.ConfigPath));
    }

    [Fact]
    public void Init_IsIdempotentAndDoesNotOverwriteExistingConfig()
    {
        using var sandbox = new SandboxHome();
        var home = sandbox.Home;
        Directory.Delete(home.Root, recursive: true);

        var firstRun = CliApp.Run(["--home", home.Root, "init"], new StringWriter(), new StringWriter());
        Assert.Equal(0, firstRun);

        const string sentinel = "# sentinel: do not overwrite me\n";
        File.WriteAllText(home.ConfigPath, sentinel);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var secondRun = CliApp.Run(["--home", home.Root, "init"], stdout, stderr);

        Assert.Equal(0, secondRun);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Equal(sentinel, File.ReadAllText(home.ConfigPath));
        Assert.Contains($"{home.ConfigPath} already exists", stdout.ToString());
    }
}
