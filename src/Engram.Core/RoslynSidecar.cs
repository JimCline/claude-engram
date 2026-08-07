using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>
/// Drives the tier-2 C# analyzer (D1/D24): a separate <c>engram-roslyn</c> process spoken
/// to over stdin/stdout, never over the database. Everything here degrades to tier 0 —
/// a missing sidecar, a missing runtime, a timeout, a crash mid-batch, or one unparseable
/// file each cost exactly the deep analysis and nothing else, silently, because an
/// optional tier that can fail an index run is not optional.
/// </summary>
public static class RoslynSidecar
{
    public const string EnvironmentOverride = "ENGRAM_ROSLYN_SIDECAR";

    /// <summary>
    /// The sidecar binary, or null for "this machine indexes C# at tier 0". The env var
    /// wins so tests and unusual installs can say exactly which binary; otherwise it
    /// lives beside the executable, or in <c>roslyn/</c> beside it — install.sh publishes
    /// it there so the framework-dependent dependency closure stays out of the bin
    /// directory. An override that points at nothing means no sidecar, never a fallback:
    /// a broken explicit configuration should not silently become a different one.
    /// </summary>
    public static string? Locate(Func<string, string?> environment, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment(EnvironmentOverride) is { Length: > 0 } overridePath)
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        var root = baseDirectory ?? AppContext.BaseDirectory;
        var name = OperatingSystem.IsWindows() ? "engram-roslyn.exe" : "engram-roslyn";

        var beside = Path.Combine(root, name);
        if (File.Exists(beside))
        {
            return beside;
        }

        var installed = Path.Combine(root, "roslyn", name);
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>
    /// One process for the whole batch — the ~40 ms start is paid once, not per file.
    /// Returns null when the sidecar could not run at all; a partial dictionary when it
    /// died mid-batch, which the caller treats as per-file fallback.
    /// </summary>
    public static Dictionary<string, DeepAnalysis>? Analyze(
        string sidecarPath,
        IReadOnlyList<(string RelativePath, string Content)> files,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(sidecarPath);
        ArgumentNullException.ThrowIfNull(files);

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(sidecarPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            // Read both streams concurrently with the writes: the child answers line by
            // line, and a full stdout pipe would deadlock against our unfinished stdin.
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            try
            {
                foreach (var (relativePath, content) in files)
                {
                    process.StandardInput.WriteLine(new JsonObject
                    {
                        ["path"] = relativePath,
                        ["content"] = content,
                    }.ToJsonString());
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child died while we wrote; whatever it answered first still counts.
            }

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            var results = new Dictionary<string, DeepAnalysis>(StringComparer.Ordinal);
            foreach (var line in stdout.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Parse(line) is { } analysis)
                {
                    results[analysis.Path] = analysis;
                }
            }

            return results;
        }
    }

    private static DeepAnalysis? Parse(string line)
    {
        JsonObject? record;
        try
        {
            record = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (record?["path"]?.GetValue<string>() is not { } path)
        {
            return null;
        }

        if (record["error"]?.GetValue<string>() is { } error)
        {
            return new DeepAnalysis(path, [], [], error);
        }

        var symbols = new List<DeepSymbol>();
        if (record["symbols"] is JsonArray symbolArray)
        {
            foreach (var node in symbolArray)
            {
                if (node is JsonObject symbol
                    && symbol["name"]?.GetValue<string>() is { Length: > 0 } name
                    && symbol["declaration"]?.GetValue<string>() is { } declaration)
                {
                    symbols.Add(new DeepSymbol(
                        name,
                        symbol["kind"]?.GetValue<string>() ?? "type",
                        declaration,
                        symbol["doc"]?.GetValue<string>(),
                        symbol["scope"]?.GetValue<string>(),
                        symbol["params"]?.GetValue<string>()));
                }
            }
        }

        var imports = new List<string>();
        if (record["imports"] is JsonArray importArray)
        {
            foreach (var node in importArray)
            {
                if (node?.GetValue<string>() is { Length: > 0 } import)
                {
                    imports.Add(import);
                }
            }
        }

        return new DeepAnalysis(path, symbols, imports, null);
    }
}
