using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Engram.EndToEnd.Tests;

/// <summary>
/// §4 of docs/specs/close-graph-query-gap.md: NEEDS-EVIDENCE — does `navigate callers/callees`
/// cost scale with corpus size (H1: <c>MatchingSymbolNames</c> full-scans every symbol-name
/// entity; H2: <c>Callees</c> calls <c>SymbolResolver.Resolve</c> once per callee)?
/// </summary>
/// <remarks>
/// <para>
/// Gated behind <see cref="RunEnvVar"/> rather than running in ordinary CI: it seeds real
/// 5k/50k-function JavaScript repos through the published binary's own <c>index --apply</c> path
/// (never through Engram.Core directly — this project deliberately has no reference to it, or a
/// tier-3 test stops proving anything about the binary that ships) and drives the server over
/// HTTP MCP, which for the 50k corpus takes minutes. It is an experiment whose numbers get
/// written up by hand, not a repeatable regression assertion — the decision rule in the spec is
/// read by a person against the printed table, not asserted here, because a hard latency
/// threshold in an environment-dependent test is exactly the kind of check people learn to route
/// around (CLAUDE.md's D37 rule, applied to a benchmark).
/// </para>
/// <para>
/// Deviation from the spec's literal "3 shapes × 4 relations": <c>defined_at</c>/<c>imports</c>
/// cost is a single <c>SymbolResolver.Resolve</c> call with no hypothesis attached here, already
/// characterized by D58 for the same query shape in recall. Noted rather than silently narrowed.
/// </para>
/// <para>
/// §11.2 (Architect ruling on the qualifier-spelling fix) adds an <c>implementers</c> arm: it
/// moved from one indexed equality to a <see cref="Engram.Core.CodeCallGraph.MatchingSymbolNames"/>
/// scan, the same mechanism this file already measures for <c>callers</c>/<c>callees</c>, and
/// §9.4 priced that move at +17.80 ms @50k — a number this harness exists to check rather than
/// take on faith. <see cref="GenerateRepo"/> now also emits <c>class … extends …</c> declarations
/// at the same stride as the hub/distinctive callers, so the same corpus serves both measurements.
/// </para>
/// <para>
/// The original H2 pass had a known gap: its <c>callees</c> "hub" arm (query <c>Fn_0</c>) has
/// exactly one callee, so it never actually exercised "one subject with many callees." A
/// dedicated <c>FanOutFn</c> (calling <see cref="FanOutTargetCount"/> distinct, genuinely
/// declared functions) closes that gap as the <c>high-fanout</c> arm; the existing
/// <c>distinctive</c> arm (<c>Fn_1</c>, one callee) already serves as the low-fanout comparison.
/// </para>
/// <para>
/// docs/specs/callees-fanout-resolution.md §2 (NE-2): <c>FanOutFn</c>'s callees each resolve
/// to exactly one candidate (M=1), the cheapest case for the per-candidate loop. A dedicated
/// <c>HighMFanOutFn</c> calls common leaf names (<see cref="CommonLeafNames"/>) that each
/// collide with <see cref="CandidatesPerCommonLeaf"/> genuinely declared candidates, as the
/// <c>high-m-fanout</c> arm, to measure the case real dispatch code produces.
/// </para>
/// </remarks>
public class NavigateLatencyMeasurementTests
{
    private const string RunEnvVar = "ENGRAM_RUN_LATENCY_BENCHMARK";
    private const int FunctionsPerFile = 100;
    private const int HubCallerStride = 50;
    private const int FanOutTargetCount = 200;
    private const int CandidatesPerCommonLeaf = 50;
    private const int CallSitesPerCommonLeaf = 5;

    private static readonly string[] CommonLeafNames = ["Get", "Add", "Run", "Dispose"];

    private static readonly string[] HubSpellings =
    [
        "Hub", "a.Hub", "b.Hub", "cache.Hub", "store.Hub", "svc.Hub", "mgr.Hub", "ctx.Hub",
        "obj.Hub", "sys.Hub", "app.Hub", "db.Hub", "net.Hub", "io.Hub", "fs.Hub", "os.Hub",
        "env.Hub", "conf.Hub", "log.Hub", "util.Hub",
    ];

    private const string NoMatchQuery = "ZzzAbsentSymbolNotInStore";
    private const string DistinctiveQuery = "DistinctiveTargetFn";
    private const string HubQuery = "Hub";

    [Fact]
    public async Task Navigate_CallersAndCallees_LatencyAcrossCorpusScale()
    {
        Assert.SkipUnless(EndToEndBinary.Path is not null, EndToEndBinary.SkipReason);
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(RunEnvVar) == "1",
            $"Benchmark, not a regression test; set {RunEnvVar}=1 to run it (seeds 5k/50k-function " +
            "corpora through the published binary and drives it over HTTP — several minutes).");

        var cancellationToken = TestContext.Current.CancellationToken;
        var report = new StringBuilder();
        report.AppendLine("# navigate callers/callees latency — §4 measurement");
        report.AppendLine();

        var grammarLibDir = await EnsureTreeSitterGrammarsAsync();

        foreach (var scale in new[] { 5_000, 50_000 })
        {
            var (indexElapsedMs, arms) = await MeasureScaleAsync(scale, grammarLibDir, cancellationToken);
            report.AppendLine($"## {scale:N0} functions");
            report.AppendLine();
            report.AppendLine($"Full re-index wall clock: {indexElapsedMs:F0} ms.");
            report.AppendLine();
            report.AppendLine("| relation | shape | median ms | floor-subtracted ms |");
            report.AppendLine("|---|---|---|---|");
            var floor = arms.First(a => a.Shape == "no-match").MedianMs;
            foreach (var arm in arms)
            {
                report.AppendLine(
                    $"| {arm.Relation} | {arm.Shape} | {arm.MedianMs:F2} | {arm.MedianMs - floor:F2} |");
            }

            report.AppendLine();
        }

        Console.WriteLine(report.ToString());
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetTempPath(), "engram-navigate-latency-results.md"), report.ToString(), cancellationToken);
    }

    private sealed record Arm(string Relation, string Shape, double MedianMs);

    private static async Task<(double IndexElapsedMs, IReadOnlyList<Arm> Arms)> MeasureScaleAsync(
        int functionCount, string grammarLibDir, CancellationToken cancellationToken)
    {
        using var home = new TestHome();
        Directory.CreateDirectory(Path.Combine(home.Root, "lib"));
        foreach (var lib in Directory.EnumerateFiles(grammarLibDir))
        {
            File.Copy(lib, Path.Combine(home.Root, "lib", Path.GetFileName(lib)), overwrite: true);
        }

        var repo = GenerateRepo(functionCount);
        try
        {
            var indexStopwatch = Stopwatch.StartNew();
            var (indexExit, indexOut, indexErr) = await RunIndexAsync(home.Root, repo, cancellationToken);
            indexStopwatch.Stop();
            Assert.True(indexExit == 0, $"engram index --apply failed: {indexOut} {indexErr}");

            AssertCorpusSeeded(home.Root, functionCount);

            var port = FreeTcpPort.Next();
            var (startExit, _, startErr) = EngramProcess.Run(home.Root, "start", "--port", port.ToString());
            Assert.True(startExit == 0, $"engram start failed: {startErr}");

            try
            {
                using var client = new HttpMcpClient(port);
                await client.InitializeAsync(cancellationToken);

                // Every (relation, shape) pair as one arm; two independent labels for the same
                // "distinctive" query calibrate the harness against its own noise (D58's protocol).
                var specs = new (string Relation, string Shape, string Query)[]
                {
                    ("callers", "no-match", NoMatchQuery),
                    ("callers", "distinctive", DistinctiveQuery),
                    ("callers", "distinctive-b", DistinctiveQuery),
                    ("callers", "hub", HubQuery),
                    ("callees", "no-match", NoMatchQuery),
                    ("callees", "distinctive", "Fn_1"),
                    ("callees", "distinctive-b", "Fn_1"),
                    ("callees", "hub", "Fn_0"),
                    ("callees", "high-fanout", "FanOutFn"),
                    ("callees", "high-m-fanout", "HighMFanOutFn"),
                    ("implementers", "no-match", NoMatchQuery),
                    ("implementers", "distinctive", DistinctiveQuery),
                    ("implementers", "distinctive-b", DistinctiveQuery),
                    ("implementers", "hub", HubQuery),
                };

                // Warm up every arm once before timing any of them, then alternate arms on every
                // timed iteration rather than running one arm to completion before the next —
                // running the same arm first every time charges it whatever the first of a pair
                // costs (the +0.78ms trap this repo already paid for once, D-latency-protocol).
                foreach (var spec in specs)
                {
                    await client.CallToolTextAsync(
                        "engram_navigate",
                        new JsonObject { ["query"] = spec.Query, ["relation"] = spec.Relation },
                        cancellationToken);
                }

                const int iterations = 7;
                var samples = specs.ToDictionary(s => (s.Relation, s.Shape), _ => new List<double>());
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    foreach (var spec in specs)
                    {
                        var stopwatch = Stopwatch.StartNew();
                        await client.CallToolTextAsync(
                            "engram_navigate",
                            new JsonObject { ["query"] = spec.Query, ["relation"] = spec.Relation },
                            cancellationToken);
                        stopwatch.Stop();
                        samples[(spec.Relation, spec.Shape)].Add(stopwatch.Elapsed.TotalMilliseconds);
                    }
                }

                var arms = specs
                    .Select(s => new Arm(s.Relation, s.Shape, Median(samples[(s.Relation, s.Shape)])))
                    .ToList();
                return (indexStopwatch.Elapsed.TotalMilliseconds, arms);
            }
            finally
            {
                EngramProcess.Run(home.Root, "stop");
            }
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="EngramProcess.Run(string, string[])"/> bounds every invocation to 10 seconds,
    /// which is correct for the one-shot commands the rest of this suite drives but far too short
    /// for indexing a 50k-function corpus. This is that call, unbounded, kept local rather than
    /// widening the shared helper's timeout for every other tier-3 test.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunIndexAsync(
        string home, string repo, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(EndToEndBinary.Path!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("index");
        startInfo.ArgumentList.Add(repo);
        startInfo.ArgumentList.Add("--apply");
        startInfo.Environment["ENGRAM_HOME"] = home;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start engram index.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    /// <summary>
    /// Fails loudly rather than timing an empty scan — this repo has been bitten twice by a
    /// fixture that looked seeded and was not (the details-less equivalence fixture, the
    /// TokenIndex guard that passed with the defect in place; §4 names this as step zero).
    /// </summary>
    private static void AssertCorpusSeeded(string home, int expectedFunctions)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(home, "engram.db")}");
        connection.Open();

        using var symbolNames = connection.CreateCommand();
        symbolNames.CommandText = "SELECT COUNT(*) FROM entity WHERE kind = 'symbol-name';";
        var symbolNameCount = (long)symbolNames.ExecuteScalar()!;
        var minExpectedSymbolNames = (long)(expectedFunctions * 0.9);
        Assert.True(
            symbolNameCount >= minExpectedSymbolNames,
            $"expected roughly {expectedFunctions} symbol-name entities (>= {minExpectedSymbolNames}), " +
            $"found {symbolNameCount} — the fixture did not seed what this measurement needs. A handful " +
            "fewer than the function count is expected: hub/distinctive callers reuse a shared callee " +
            "spelling instead of minting their own.");

        using var calls = connection.CreateCommand();
        calls.CommandText = "SELECT COUNT(*) FROM fact WHERE predicate = 'calls' AND valid_to IS NULL;";
        var callsCount = (long)calls.ExecuteScalar()!;
        Assert.True(callsCount >= minExpectedSymbolNames, $"expected roughly {expectedFunctions} live calls facts, found {callsCount}.");

        using var inherits = connection.CreateCommand();
        inherits.CommandText =
            "SELECT COUNT(*) FROM fact WHERE predicate IN ('inherits', 'implements', 'derives-from') AND valid_to IS NULL;";
        var inheritsCount = (long)inherits.ExecuteScalar()!;
        var minExpectedInherits = (long)(expectedFunctions / (double)HubCallerStride * 0.5);
        Assert.True(
            inheritsCount >= minExpectedInherits,
            $"expected at least {minExpectedInherits} live inheritance facts (hub/distinctive classes " +
            $"seeded at the same stride as hub/distinctive callers), found {inheritsCount} — the fixture " +
            "did not seed what the implementers arm needs.");
    }

    private static string GenerateRepo(int functionCount)
    {
        var repo = Path.Combine(Path.GetTempPath(), "engram-navlat-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);

        var fileCount = Math.Max(1, functionCount / FunctionsPerFile);
        var index = 0;
        for (var file = 0; file < fileCount; file++)
        {
            var builder = new StringBuilder();
            for (var k = 0; k < FunctionsPerFile && index < functionCount; k++, index++)
            {
                if (index == 0)
                {
                    builder.AppendLine("function Hub() {}");
                    builder.AppendLine("function DistinctiveTargetFn() {}");
                    builder.AppendLine("class Hub {}");
                    builder.AppendLine("class DistinctiveTargetFn {}");
                }

                string body;
                string? baseSpelling;
                if (index > 0 && index % HubCallerStride == 0)
                {
                    body = HubSpellings[(index / HubCallerStride) % HubSpellings.Length] + "();";
                    baseSpelling = HubSpellings[(index / HubCallerStride) % HubSpellings.Length];
                }
                else if (index is 1 or 2)
                {
                    body = "DistinctiveTargetFn();";
                    baseSpelling = "DistinctiveTargetFn";
                }
                else
                {
                    body = $"Callee_{index}();";
                    baseSpelling = null;
                }

                builder.AppendLine($"function Fn_{index}() {{ {body} }}");
                if (baseSpelling is not null)
                {
                    builder.AppendLine($"class Cls_{index} extends {baseSpelling} {{}}");
                }
            }

            File.WriteAllText(Path.Combine(repo, $"f{file}.js"), builder.ToString());
        }

        var fanOut = new StringBuilder();
        for (var i = 0; i < FanOutTargetCount; i++)
        {
            fanOut.AppendLine($"function FanTarget_{i}() {{}}");
        }

        fanOut.Append("function FanOutFn() { ");
        for (var i = 0; i < FanOutTargetCount; i++)
        {
            fanOut.Append($"FanTarget_{i}(); ");
        }

        fanOut.AppendLine("}");
        File.WriteAllText(Path.Combine(repo, "fanout.js"), fanOut.ToString());

        // NE-2 (docs/specs/callees-fanout-resolution.md §6): FanOutFn above resolves every
        // callee on the first tier with exactly one candidate each (M=1) -- the best case for
        // the per-row resolve loop. Real dispatch code calls common leaf names (Get/Add/Run/
        // Dispose) that collide with many declarations (M large), so each call site's inner
        // loop runs CandidatesPerCommonLeaf times instead of once. Each leaf's declarations
        // live in their own file so they mint distinct entity paths.
        var fanOutMDir = Path.Combine(repo, "fanout-m");
        Directory.CreateDirectory(fanOutMDir);
        foreach (var leaf in CommonLeafNames)
        {
            for (var i = 0; i < CandidatesPerCommonLeaf; i++)
            {
                File.WriteAllText(
                    Path.Combine(fanOutMDir, $"{leaf}_{i}.js"), $"function {leaf}() {{}}\n");
            }
        }

        var highMCaller = new StringBuilder();
        highMCaller.Append("function HighMFanOutFn() { ");
        foreach (var leaf in CommonLeafNames)
        {
            for (var q = 0; q < CallSitesPerCommonLeaf; q++)
            {
                highMCaller.Append($"q{q}.{leaf}(); ");
            }
        }

        highMCaller.AppendLine("}");
        File.WriteAllText(Path.Combine(fanOutMDir, "caller.js"), highMCaller.ToString());

        return repo;
    }

    private static async Task<string> EnsureTreeSitterGrammarsAsync()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "engram-navlat-treesitter-cache");
        var libDir = Path.Combine(cacheDir, "lib");
        if (Directory.Exists(libDir) && Directory.EnumerateFiles(libDir).Any())
        {
            return libDir;
        }

        var repoRoot = FindRepoRoot();
        var script = Path.Combine(repoRoot, "scripts", "fetch-tree-sitter.sh");
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("--home");
        startInfo.ArgumentList.Add(cacheDir);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start fetch-tree-sitter.sh.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"fetch-tree-sitter.sh failed: {stdout} {stderr}");

        return libDir;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root above " + AppContext.BaseDirectory);
    }
}
