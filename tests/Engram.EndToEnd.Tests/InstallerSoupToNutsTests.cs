using System.Runtime.InteropServices;
using static Engram.EndToEnd.Tests.InstallerHarness;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// The soup-to-nuts additions to the installers: SDK bootstrap, llama natives, and the
/// PowerShell pair. The original round-trip suite is <see cref="InstallerRoundTripTests"/>.
/// </summary>
public class InstallerSoupToNutsTests
{
    private static string CurrentRid =>
        (OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture == Architecture.Arm64) switch
        {
            (true, _, true) => "osx-arm64",
            (true, _, false) => "osx-x64",
            (_, true, true) => "win-arm64",
            (_, true, false) => "win-x64",
            (_, _, true) => "linux-arm64",
            (_, _, false) => "linux-x64",
        };

    // --- The SDK decision, driven through stubs so it is the same test on every machine ---

    [Fact]
    public void DryRun_WithANet10SdkOnPath_PlansThePublishAndNoBootstrap()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "install.sh is the POSIX installer.");

        using var home = new InstallerTestHome();
        home.StubDotnet("10.0.100");

        var result = RunScript("install.sh", home.Root, "--prefix", home.Prefix);

        Assert.True(result.ExitCode == 0, $"dry run failed: {result.Stderr}");
        Assert.Contains("dotnet publish", result.Stdout);
        Assert.DoesNotContain("install the .NET 10 SDK", result.Stdout);
    }

    [Fact]
    public void DryRun_WithOnlyAnOlderSdk_PlansTheBootstrapIntoTheSdkDir()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "install.sh is the POSIX installer.");

        using var home = new InstallerTestHome();
        home.StubDotnet("8.0.100");
        var sdkDir = Path.Combine(home.Root, "sdk");

        var result = RunScript("install.sh", home.Root, "--prefix", home.Prefix, "--sdk-dir", sdkDir);

        Assert.True(result.ExitCode == 0, $"dry run failed: {result.Stderr}");
        Assert.Contains($"install the .NET 10 SDK into {sdkDir}", result.Stdout);
        Assert.Contains($"{sdkDir}/dotnet publish", result.Stdout);
    }

    // What an SDK-8 machine used to get was a pass through preflight ("dotnet is on
    // PATH") and then an opaque publish failure. The chain proven here is the fix:
    // the version decides, the bootstrap runs with the arguments Microsoft's script
    // needs, and the build is attempted with the dotnet the bootstrap produced — not
    // whatever PATH answers. The planted dotnet's publish fails on purpose; a real
    // publish belongs to the machines that run the real installer.
    [Fact]
    public void Apply_WithOnlyAnOlderSdk_BootstrapsThenBuildsWithTheBootstrappedDotnet()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "install.sh is the POSIX installer.");

        using var home = new InstallerTestHome();
        home.StubDotnet("8.0.100");
        var argvLog = home.StubDotnetInstall();
        var sdkDir = Path.Combine(home.Root, "sdk");

        var result = RunScript(
            "install.sh", home.Root,
            "--apply", "--prefix", home.Prefix, "--sdk-dir", sdkDir,
            "--dotnet-install", home.StubDotnetInstallPath);

        Assert.True(result.ExitCode != 0, "the planted dotnet cannot publish, so the install must fail");
        Assert.True(File.Exists(argvLog), "the provided dotnet-install script was never invoked");

        var argv = File.ReadAllText(argvLog);
        Assert.Contains("--channel 10.0", argv);
        Assert.Contains($"--install-dir {sdkDir}", argv);
        Assert.Contains("--no-path", argv);

        Assert.True(File.Exists(Path.Combine(sdkDir, "dotnet")), "the bootstrap should have produced a dotnet in the SDK dir");
        Assert.Contains("the bootstrapped stub dotnet cannot publish", result.Stdout + result.Stderr);
        Assert.False(File.Exists(home.BinaryPath), "a failed build must not have installed anything");
    }

    // --- The llama natives survive the install, and uninstall removes exactly them ---

    [Fact]
    public void Install_CarriesTheLlamaNatives_AndUninstallRemovesExactlyThem()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "install.sh is the POSIX installer.");
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        var nativesSource = Path.Combine(Path.GetDirectoryName(EndToEndBinary.Path!)!, "runtimes", CurrentRid, "native");
        Assert.SkipUnless(
            Directory.Exists(nativesSource),
            "the published binary has no runtimes tree beside it, so there is nothing for the install to carry.");

        using var home = new InstallerTestHome();
        File.WriteAllText(home.ZshrcPath, "# seeded\n");

        var install = RunScript("install.sh", home.Root, "--apply", "--binary", EndToEndBinary.Path!, "--prefix", home.Prefix, "--no-tree-sitter", "--no-sqlite-vec");
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}");

        var installedNatives = Path.Combine(home.Prefix, "runtimes", CurrentRid, "native");
        var manifest = Path.Combine(home.Prefix, "runtimes", ".engram-manifest");
        Assert.True(Directory.Exists(installedNatives), "runtimes/<rid>/native should exist under the prefix after install");
        Assert.NotEmpty(Directory.GetFiles(installedNatives, "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(manifest), "install should have recorded a manifest for uninstall");

        // Not ours, so it must survive — the manifest is what separates removal from rm -rf.
        var foreign = Path.Combine(home.Prefix, "runtimes", "foreign.txt");
        File.WriteAllText(foreign, "someone else's file");

        var uninstall = RunScript("uninstall.sh", home.Root, "--apply", "--prefix", home.Prefix);
        Assert.True(uninstall.ExitCode == 0, $"uninstall failed: {uninstall.Stderr}");

        Assert.False(Directory.Exists(installedNatives), "uninstall should have removed the natives it recorded");
        Assert.False(File.Exists(manifest), "uninstall should have removed the manifest itself");
        Assert.True(File.Exists(foreign), "a file the manifest does not list must survive the uninstall");
    }

    // --- The PowerShell pair ---

    // pwsh parses the whole file before running a line of it, so -Help exiting zero is a
    // syntax gate for the entire script on every OS — which is the only gate available
    // where these scripts cannot run for real.
    [Fact]
    public void InstallPs1_Help_ParsesEverywhereAndExitsZero()
    {
        Assert.SkipUnless(PwshPath is not null, PwshSkipReason);

        using var home = new InstallerTestHome();

        var result = RunPwshScript("install.ps1", home.Root, "-Help");

        Assert.True(result.ExitCode == 0, $"install.ps1 -Help failed: {result.Stderr}");
        Assert.Contains("Usage:", result.Stdout);
    }

    [Fact]
    public void UninstallPs1_Help_ParsesEverywhereAndExitsZero()
    {
        Assert.SkipUnless(PwshPath is not null, PwshSkipReason);

        using var home = new InstallerTestHome();

        var result = RunPwshScript("uninstall.ps1", home.Root, "-Help");

        Assert.True(result.ExitCode == 0, $"uninstall.ps1 -Help failed: {result.Stderr}");
        Assert.Contains("Usage:", result.Stdout);
    }

    [Fact]
    public void InstallPs1_OnAPosixOs_RefusesAndNamesTheShellInstaller()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "this is the wrong-OS refusal; on Windows the script proceeds.");
        Assert.SkipUnless(PwshPath is not null, PwshSkipReason);

        using var home = new InstallerTestHome();

        var result = RunPwshScript("install.ps1", home.Root);

        Assert.True(result.ExitCode != 0, "install.ps1 must refuse to run on a POSIX OS");
        Assert.Contains("install.sh", result.Stdout + result.Stderr);
    }

    [Fact]
    public void InstallPs1_DryRun_ChangesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "install.ps1 only proceeds on Windows.");
        Assert.SkipUnless(PwshPath is not null, PwshSkipReason);
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var prefix = Path.Combine(home.Root, "programs", "engram");

        var result = RunPwshScript("install.ps1", home.Root, "-Binary", EndToEndBinary.Path!, "-Prefix", prefix);

        Assert.True(result.ExitCode == 0, $"dry run failed: {result.Stderr}");
        Assert.Contains("Dry run only", result.Stdout);
        Assert.Contains("user PATH", result.Stdout);
        Assert.False(Directory.Exists(prefix), "a dry run must not create the prefix");
        Assert.False(Directory.Exists(Path.Combine(home.Root, ".engram")), "a dry run must not create the Engram home");
    }

    // -NoPath, deliberately: the runner's user PATH lives in the registry, where there is
    // no sandbox to redirect it into, and mutating it is touching the real instance. The
    // PATH edit therefore ships proven only as a dry-run plan (the test above), and this
    // round trip proves everything else the Windows installer does.
    [Fact]
    public void InstallPs1_Apply_WithBinary_RoundTripsThroughUninstall()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "install.ps1 only proceeds on Windows.");
        Assert.SkipUnless(PwshPath is not null, PwshSkipReason);
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new InstallerTestHome();
        var prefix = Path.Combine(home.Root, "programs", "engram");
        var binaryTarget = Path.Combine(prefix, "engram.exe");

        var install = RunPwshScript("install.ps1", home.Root, "-Apply", "-NoPath", "-Binary", EndToEndBinary.Path!, "-Prefix", prefix);
        Assert.True(install.ExitCode == 0, $"install failed: {install.Stderr}\n{install.Stdout}");

        Assert.True(File.Exists(binaryTarget), "binary should exist after install");
        Assert.True(File.Exists(Path.Combine(home.Root, ".engram", "config.toml")), "init should have created the sandboxed home");

        var sidecar = Path.Combine(prefix, "e_sqlite3.dll");
        Assert.True(File.Exists(sidecar), "the SQLite sidecar should have been installed beside the binary");

        var nativesSource = Path.Combine(Path.GetDirectoryName(EndToEndBinary.Path!)!, "runtimes", CurrentRid, "native");
        if (Directory.Exists(nativesSource))
        {
            Assert.True(
                File.Exists(Path.Combine(prefix, "runtimes", ".engram-manifest")),
                "the llama natives were beside the binary but the install recorded no manifest");
        }

        var uninstall = RunPwshScript("uninstall.ps1", home.Root, "-Apply", "-Prefix", prefix);
        Assert.True(uninstall.ExitCode == 0, $"uninstall failed: {uninstall.Stderr}\n{uninstall.Stdout}");

        Assert.False(File.Exists(binaryTarget), "binary should be gone after uninstall");
        Assert.False(File.Exists(sidecar), "sidecar should be gone after uninstall");
        Assert.False(Directory.Exists(Path.Combine(prefix, "runtimes")), "the natives tree should be gone after uninstall");
        Assert.True(Directory.Exists(Path.Combine(home.Root, ".engram")), "the Engram home must survive an uninstall without -Purge");
    }
}
