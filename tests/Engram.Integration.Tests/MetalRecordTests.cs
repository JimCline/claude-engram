using System.Runtime.InteropServices;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The record a load leaves behind about ggml-metal, and the doctor row that reads it (D28).
/// </summary>
/// <remarks>
/// Everything here except the load itself runs without weights, because the record is a file and
/// the row is a reader. The one test that needs a real model lives in <see cref="LocalRuntimeTests"/>
/// with the rest of the weights-gated half.
/// </remarks>
[Collection(SqlitePoolCollection.Name)]
public sealed class MetalRecordTests
{
    /// <summary>
    /// What ggml-metal prints, in the shape it prints it — including the decoy.
    /// </summary>
    /// <remarks>
    /// The <c>GPU name:</c> line is here on purpose. Measured on an M5 Pro, ggml-metal answers it
    /// with <c>MTL0</c>, a device index, while the hardware appears only on the init line. A parser
    /// keyed to the obvious-looking line would read every Mac as unidentifiable, and the warning
    /// that depends on identifying it could never fire.
    /// </remarks>
    private static IReadOnlyList<string> MetalLines(bool tensor, string device) =>
    [
        "ggml_metal_device_init: GPU name:   MTL0",
        "ggml_metal_device_init: has unified memory    = true",
        $"ggml_metal_device_init: has tensor            = {(tensor ? "true" : "false")}",
        $"ggml_metal_init: picking default device: {device}",
    ];

    private static void RequireMetalPlatform() =>
        Assert.SkipUnless(
            OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
            "the metal row is only emitted on macOS arm64.");

    private static void WriteConfig(SandboxHome sandbox, string body) =>
        File.WriteAllText(sandbox.Home.ConfigPath, body);

    private static void WriteLocalProvider(SandboxHome sandbox) =>
        WriteConfig(
            sandbox,
            $"[embedding]\nprovider = \"local\"\nmodel = \"{EmbeddingModels.Default.Id}\"\n");

    private static DiagnosticReport Run(SandboxHome sandbox) =>
        Diagnostics.Run(
            sandbox.Home,
            _ => null,
            repoRoot: null,
            reachOut: false,
            claudeSettingsPath: Path.Combine(sandbox.Home.Root, "claude-settings.json"));

    private static Diagnosis MetalRowFor(bool tensor, string device)
    {
        using var sandbox = new SandboxHome();
        WriteLocalProvider(sandbox);
        MetalRecord.Write(sandbox.Home, MetalLines(tensor, device));

        return Run(sandbox).Checks.Single(check => check.Name == "metal");
    }

    // -- the row --

    [Fact]
    public void MetalRow_WarnsOnlyWhenTensorOffOnCapableHardware()
    {
        RequireMetalPlatform();

        var lost = MetalRowFor(tensor: false, device: "Apple M5 Pro");
        Assert.Equal(DiagnosisState.Warn, lost.State);
        Assert.Contains("SDK", lost.Fix ?? string.Empty, StringComparison.Ordinal);

        // An M2 has no tensor cores to lose, so reporting its absence as a problem would be telling
        // the user to go fix their hardware.
        var neverHad = MetalRowFor(tensor: false, device: "Apple M2");
        Assert.Equal(DiagnosisState.Ok, neverHad.State);
        Assert.Null(neverHad.Fix);

        Assert.Equal(DiagnosisState.Ok, MetalRowFor(tensor: true, device: "Apple M5 Pro").State);
    }

    [Fact]
    public void MetalRow_UnidentifiedHardware_StaysQuiet()
    {
        RequireMetalPlatform();

        var row = MetalRowFor(tensor: false, device: "some future device");

        Assert.Equal(DiagnosisState.Ok, row.State);
        Assert.Null(row.Fix);
    }

    [Fact]
    public void MetalRow_AbsentRecord_SaysNotYetObserved()
    {
        RequireMetalPlatform();

        using var sandbox = new SandboxHome();
        WriteLocalProvider(sandbox);

        var row = Run(sandbox).Checks.Single(check => check.Name == "metal");

        Assert.Equal(DiagnosisState.Warn, row.State);
        Assert.Contains("not yet observed", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MetalRow_IsAbsentWithoutALocalProvider()
    {
        using var sandbox = new SandboxHome();
        WriteConfig(sandbox, "[embedding]\nprovider = \"none\"\n");
        MetalRecord.Write(sandbox.Home, MetalLines(tensor: false, device: "Apple M5 Pro"));

        // No row rather than Off: Off would claim the user turned Metal off, which is not a choice
        // anyone makes.
        Assert.DoesNotContain(Run(sandbox).Checks, check => check.Name == "metal");
    }

    [Fact]
    public void TheRow_NeverFailsTheExitCode()
    {
        RequireMetalPlatform();

        using var sandbox = new SandboxHome();
        WriteLocalProvider(sandbox);
        MetalRecord.Write(sandbox.Home, MetalLines(tensor: false, device: "Apple M5 Pro"));

        // Asserted on the row rather than on the report's health, because a sandbox configured for
        // local embedding with no weights in it has a Broken embedding row for reasons that have
        // nothing to do with this check. Broken is the only state that sets exit 1, and losing half
        // your Metal throughput is worth saying and not worth failing over.
        var broken = Run(sandbox).Checks
            .Where(check => check.State is DiagnosisState.Broken)
            .Select(check => check.Name);

        Assert.DoesNotContain("metal", broken);
    }

    // -- the record --

    [Fact]
    public void TheRecord_RoundTripsWhatGgmlMetalSaid()
    {
        using var sandbox = new SandboxHome();
        MetalRecord.Write(sandbox.Home, MetalLines(tensor: true, device: "Apple M5 Pro"));

        var record = MetalRecord.Read(sandbox.Home);

        Assert.NotNull(record);
        Assert.True(record.HasTensor);
        Assert.Equal("Apple M5 Pro", record.Gpu);
        Assert.Contains(record.Lines, line => line.Contains("has tensor", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDeviceName_ComesFromTheInitLineRatherThanTheDeviceIndex()
    {
        using var sandbox = new SandboxHome();
        MetalRecord.Write(sandbox.Home, MetalLines(tensor: false, device: "Apple M5 Pro"));

        var record = MetalRecord.Read(sandbox.Home);

        Assert.NotNull(record);
        Assert.NotEqual("MTL0", record.Gpu);
        Assert.Equal(5, record.AppleGeneration);
    }

    [Fact]
    public void NoMetalLines_WritesNoRecordAtAll()
    {
        using var sandbox = new SandboxHome();

        MetalRecord.Write(sandbox.Home, []);

        // This is what leaves every Linux, Windows and CUDA host with no file and so no row.
        Assert.False(File.Exists(sandbox.Home.MetalRecordPath));
    }

    [Fact]
    public void AMalformedRecord_ReadsAsAbsentRatherThanBroken()
    {
        using var sandbox = new SandboxHome();
        File.WriteAllText(sandbox.Home.MetalRecordPath, "{ this is not json");

        // Derived state the next load rewrites, so surfacing corruption would ask the user to fix
        // something that fixes itself.
        Assert.Null(MetalRecord.Read(sandbox.Home));
    }

    [Fact]
    public void EachLoad_ReplacesTheRecordRatherThanAppendingToIt()
    {
        using var sandbox = new SandboxHome();

        MetalRecord.Write(sandbox.Home, MetalLines(tensor: false, device: "Apple M5 Pro"));
        MetalRecord.Write(sandbox.Home, MetalLines(tensor: true, device: "Apple M5 Pro"));

        var record = MetalRecord.Read(sandbox.Home);

        Assert.NotNull(record);
        Assert.True(record.HasTensor);
        Assert.Single(record.Lines, line => line.Contains("has tensor", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Apple M2", 2)]
    [InlineData("Apple M5 Pro", 5)]
    [InlineData("Apple M10 Ultra", 10)]
    [InlineData("MTL0", null)]
    [InlineData("AMD Radeon Pro 5500M", null)]
    [InlineData("Apple Paravirtual device", null)]
    public void TheAppleGeneration_ParsesOrDeclinesToGuess(string gpu, int? expected)
    {
        var record = new MetalRecord(null, null, null, [], null, gpu);

        Assert.Equal(expected, record.AppleGeneration);
    }
}
