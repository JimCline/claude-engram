using System.Text.Json;
using System.Text.Json.Serialization;
using Engram.Core;

namespace Engram.Cli;

/// <summary>One check, flattened for JSON so a bug report can be pasted rather than described.</summary>
internal sealed record DoctorCheckJson(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("fix")] string? Fix);

internal sealed record DoctorReportJson(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("home")] string Home,
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("checks")] IReadOnlyList<DoctorCheckJson> Checks);

// Indented, and without the null fixes: unlike probe --json, which feeds a pipeline, this exists
// to be pasted into a bug report by someone who could not work out what was wrong.
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DoctorReportJson))]
internal sealed partial class DoctorJsonContext : JsonSerializerContext;

/// <summary>
/// Prints what is wrong with this instance, and what to type about it.
/// </summary>
/// <remarks>
/// <para><b>Exit 1 means broken, not imperfect.</b> A warning is information; an <c>off</c> row is
/// a choice. Only <see cref="DiagnosisState.Broken"/> fails, so this is safe to put in a script
/// that expects an instance with embeddings deliberately switched off to pass.</para>
///
/// <para><b>Nothing here is a state this command owns.</b> Every row comes from
/// <see cref="Diagnostics"/>, which reads the same settings, the same resolver and the same index
/// that recall does. Printing is all that lives here — a doctor that reimplemented a check would
/// eventually disagree with the code it was reporting on, and would do so silently.</para>
/// </remarks>
internal static class DoctorCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var json = false;
        var reachOut = true;
        var repoRoot = (string?)Environment.CurrentDirectory;

        foreach (var argument in args)
        {
            switch (argument)
            {
                case "--json":
                    json = true;
                    break;
                case "--offline":
                    reachOut = false;
                    break;
                case "--no-repo":
                    repoRoot = null;
                    break;
                default:
                    stderr.WriteLine($"error: unknown option {argument}");
                    CliApp.PrintUsage(stderr);
                    return 2;
            }
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var report = Diagnostics.Run(
            home,
            Environment.GetEnvironmentVariable,
            repoRoot,
            reachOut: reachOut,
            executablePath: ExecutablePath.Current);

        if (json)
        {
            WriteJson(home, report, stdout);
            return report.Healthy ? 0 : 1;
        }

        WriteText(home, report, stdout);
        return report.Healthy ? 0 : 1;
    }

    private static void WriteJson(EngramHome home, DiagnosticReport report, TextWriter stdout)
    {
        var payload = new DoctorReportJson(
            EngramVersion.Current,
            home.Root,
            report.Healthy,
            [.. report.Checks.Select(check => new DoctorCheckJson(
                check.Name,
                check.State.ToString().ToLowerInvariant(),
                check.Detail,
                check.Fix))]);

        stdout.WriteLine(JsonSerializer.Serialize(payload, DoctorJsonContext.Default.DoctorReportJson));
    }

    private static void WriteText(EngramHome home, DiagnosticReport report, TextWriter stdout)
    {
        stdout.WriteLine($"engram {EngramVersion.Current} — {home.Root}");
        stdout.WriteLine();

        var width = report.Checks.Count == 0 ? 0 : report.Checks.Max(check => check.Name.Length);

        foreach (var check in report.Checks)
        {
            stdout.WriteLine($"  {Label(check.State),-7}  {check.Name.PadRight(width)}  {check.Detail}");

            if (check.Fix is { } fix)
            {
                stdout.WriteLine($"  {new string(' ', 7)}  {new string(' ', width)}  -> {fix}");
            }
        }

        stdout.WriteLine();
        stdout.WriteLine(Summary(report));
    }

    private static string Summary(DiagnosticReport report)
    {
        if (report.Broken > 0 && report.Warnings > 0)
        {
            return $"{Count(report.Broken, "problem")}, {Count(report.Warnings, "warning")}.";
        }

        if (report.Broken > 0)
        {
            return $"{Count(report.Broken, "problem")}.";
        }

        return report.Warnings > 0
            ? $"Working. {Count(report.Warnings, "warning")}."
            : "Working.";
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Label(DiagnosisState state) => state switch
    {
        DiagnosisState.Ok => "ok",
        DiagnosisState.Off => "off",
        DiagnosisState.Warn => "warn",
        _ => "BROKEN",
    };
}
