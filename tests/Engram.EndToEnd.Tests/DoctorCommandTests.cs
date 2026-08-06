using System.Text.Json;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// Drives <c>engram doctor</c> through the published binary.
/// </summary>
/// <remarks>
/// <para>Tier 3 rather than tier 2 for two reasons the JIT build cannot reach.
/// <see cref="DoctorJson_IsWellFormedFromTheAotBinary"/> exercises a source-generated
/// <c>JsonSerializerContext</c>, which is exactly the shape that works under reflection and
/// throws once trimmed — a passing integration test proves nothing about it. And
/// <see cref="Doctor_WritesNothingIntoTheHomeItReads"/> can only be honest against a real process
/// with a real <c>ENGRAM_HOME</c>, since the read-only claim is about what the binary does to a
/// directory, not about what a method returns.</para>
/// </remarks>
public class DoctorCommandTests
{
    private static readonly string[] ExpectedChecks =
        ["home", "store", "server", "claude code", "embedding", "vector index", "backups"];

    [Fact]
    public void Doctor_OnAnInitialisedHome_ExitsZeroAndReportsEveryCheck()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "doctor", "--offline", "--no-repo");

        Assert.True(exitCode == 0, $"doctor failed on a freshly initialised home:\n{stdout}\n{stderr}");
        Assert.Equal(string.Empty, stderr);

        foreach (var check in ExpectedChecks)
        {
            Assert.Contains(check, stdout, StringComparison.Ordinal);
        }

        Assert.Contains(home.Root, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorJson_IsWellFormedFromTheAotBinary()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "doctor", "--json", "--offline", "--no-repo");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        Assert.True(root.GetProperty("healthy").GetBoolean());
        Assert.Equal(home.Root, root.GetProperty("home").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));

        var names = root.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString())
            .ToList();

        foreach (var check in ExpectedChecks)
        {
            Assert.Contains(check, names);
        }

        // Every row carries a state the renderer knows how to label; an unmapped one would print
        // as BROKEN and quietly overstate the diagnosis.
        foreach (var check in root.GetProperty("checks").EnumerateArray())
        {
            Assert.Contains(check.GetProperty("state").GetString(), (string[])["ok", "off", "warn", "broken"]);
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("detail").GetString()));
        }
    }

    /// <summary>
    /// The claim the whole design rests on, tested where it can actually be false.
    /// </summary>
    /// <remarks>
    /// Opening the store with <c>OpenInitialized</c> rather than <c>Open</c> would migrate an
    /// out-of-date schema and, per D31, snapshot it first — so this fails on a new file in
    /// <c>backups/</c> long before anyone notices the version moved.
    /// </remarks>
    [Fact]
    public void Doctor_WritesNothingIntoTheHomeItReads()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var before = Snapshot(home.Root);
        var (exitCode, _, _) = EngramProcess.Run(home.Root, "doctor", "--offline", "--no-repo");
        var after = Snapshot(home.Root);

        Assert.Equal(0, exitCode);

        var moved = before.Keys.Union(after.Keys)
            .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
            .Select(path => $"{path}: {before.GetValueOrDefault(path) ?? "(absent)"} -> {after.GetValueOrDefault(path) ?? "(absent)"}")
            .ToList();

        Assert.True(moved.Count == 0, "doctor changed the home it read:\n  " + string.Join("\n  ", moved));
    }

    [Fact]
    public void Doctor_OnAHomeThatWasNeverInitialised_ExitsOneAndSaysToRunInit()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        var root = Path.Combine(Path.GetTempPath(), "engram-e2e-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (exitCode, stdout, _) = EngramProcess.Run(root, "doctor", "--offline", "--no-repo");

            Assert.Equal(1, exitCode);
            Assert.Contains("BROKEN", stdout, StringComparison.Ordinal);
            Assert.Contains("engram init", stdout, StringComparison.Ordinal);

            // The same verdict through the other renderer. Asserted because the exit code is
            // computed once per output path, so one of them can drift to always-zero and still
            // print a report full of problems.
            var (jsonExit, jsonOut, _) = EngramProcess.Run(root, "doctor", "--json", "--offline", "--no-repo");

            Assert.Equal(1, jsonExit);
            using var document = JsonDocument.Parse(jsonOut);
            Assert.False(document.RootElement.GetProperty("healthy").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Doctor_WithAnUnknownFlag_ExitsTwoRatherThanReportingHealth()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);

        using var home = new TestHome();

        var (exitCode, stdout, stderr) = EngramProcess.Run(home.Root, "doctor", "--not-a-flag");

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--not-a-flag", stderr, StringComparison.Ordinal);
    }

    /// <summary>Every file under a root, by path, size and last write — enough to catch a rewrite.</summary>
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (!Directory.Exists(root))
        {
            return files;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            files[Path.GetRelativePath(root, path)] = $"{info.Length}@{info.LastWriteTimeUtc:O}";
        }

        return files;
    }
}
