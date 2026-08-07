using System.Globalization;
using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>
/// What ggml-metal reported about this machine the last time weights were loaded here (D28).
/// </summary>
/// <remarks>
/// <para><b>Why this is written down rather than asked for.</b> ggml-metal compiles its shaders at
/// runtime and picks the shader language version from the SDK recorded in the <i>main executable</i>,
/// so whether the tensor path is on is a property of the process that loaded llama.cpp — not of the
/// binary that happens to be asking. Measured on one M5 Pro: the same weights, the same
/// <c>libggml-metal.dylib</c>, loaded from an executable stamped <c>sdk 26.5</c> report
/// <c>has tensor = true</c>, and from one stamped <c>sdk 15.5</c> report <c>false</c>. So the
/// question has no answer in the abstract, only in a process that has loaded.</para>
///
/// <para><b>And <c>doctor</c> cannot be that process</b> — finding out means loading several hundred
/// megabytes of weights, which is exactly the readiness-check-that-loads defect D35 exists to
/// prevent, and D37 makes doctor a reader. So the loader records what it saw and doctor reads it.
/// Before any load there is no record, and doctor says so: the tensor path has no performance to
/// lose until something loads, so the blind window and the window in which the answer does not
/// matter are the same window.</para>
///
/// <para><b>Staleness is not tracked, on purpose.</b> The file is overwritten whole by the next load
/// and there is no key to check it against. A binary rebuilt with a newer SDK but not yet restarted
/// really is still running the old shaders, so the "stale" record is the true description of what is
/// serving; any freshness scheme keyed to the binary on disk would be less accurate, and keying to
/// the executable path is what D42 measured as wrong — two engram binaries legitimately serve one
/// home. <see cref="Loader"/> is therefore reported and never enforced.</para>
///
/// <para>Derived state (D8): regenerable by any load, destroying nothing authored, so deleting it is
/// free and <c>repair</c> and <c>compact</c> leave it alone.</para>
/// </remarks>
public sealed record MetalRecord(
    string? ObservedAt,
    string? Loader,
    string? Os,
    IReadOnlyList<string> Lines,
    bool? HasTensor,
    string? Gpu)
{
    private const string TensorNeedle = "has tensor";

    /// <remarks>
    /// Not <c>GPU name:</c>, which ggml-metal answers with a device index — measured: <c>MTL0</c>.
    /// The hardware appears only on the init lines, and a check keyed to the wrong one could never
    /// fire.
    /// </remarks>
    private static readonly string[] DeviceNeedles = ["picking default device:", "found device:"];

    /// <summary>
    /// The Apple silicon generation named in <see cref="Gpu"/>, or null when it does not name one.
    /// </summary>
    /// <remarks>
    /// Read from the record's own device line rather than from a sysctl or a table in Engram, so the
    /// hardware and the capability are observed at the same moment by the same code. Callers must
    /// treat null as "do not know" and stay quiet, never as "old" — a doctor that reds an M2 for
    /// tensor cores it never had is one people stop reading.
    /// </remarks>
    public int? AppleGeneration
    {
        get
        {
            const string prefix = "Apple M";

            if (Gpu is null)
            {
                return null;
            }

            var at = Gpu.IndexOf(prefix, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            var index = at + prefix.Length;
            var generation = 0;
            var digits = 0;

            while (index < Gpu.Length && char.IsAsciiDigit(Gpu[index]))
            {
                generation = (generation * 10) + (Gpu[index] - '0');
                index++;
                digits++;
            }

            return digits > 0 ? generation : null;
        }
    }

    /// <summary>Records <paramref name="lines"/> as the newest observation, replacing any older one.</summary>
    /// <remarks>
    /// Silent about its own failures: this is a diagnostic, and failing or slowing a load that would
    /// otherwise succeed in order to report on loads would be the wrong way round.
    /// </remarks>
    public static void Write(EngramHome home, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            // No Metal backend in this build, which is every Linux, Windows and CUDA host. They get
            // no file, which is what leaves them with no row rather than a row saying nothing.
            return;
        }

        try
        {
            var captured = new JsonArray();
            foreach (var line in lines)
            {
                // JsonArray.Add binds to the AOT-hostile generic overload without this cast.
                ((IList<JsonNode?>)captured).Add(JsonValue.Create(line));
            }

            var body = new JsonObject
            {
                ["observed_at"] = JsonValue.Create(
                    DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
                ["loader"] = JsonValue.Create(Environment.ProcessPath),
                ["os"] = JsonValue.Create(Environment.OSVersion.VersionString),
                ["has_tensor"] = ParseHasTensor(lines) is { } tensor ? JsonValue.Create(tensor) : null,
                ["gpu"] = JsonValue.Create(ParseGpu(lines)),
                ["lines"] = captured,
            };

            var temporary = home.MetalRecordPath + ".tmp";
            File.WriteAllText(temporary, body.ToJsonString());
            File.Move(temporary, home.MetalRecordPath, overwrite: true);
        }
#pragma warning disable CA1031 // A diagnostic that cannot be written is not a load that should fail.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The last observation, or null when there is none or it cannot be read.</summary>
    /// <remarks>
    /// Malformed is absent, not broken. This is derived state that the next load rewrites, so a
    /// corrupt file self-heals — surfacing it as a fault would ask the user to fix something that
    /// fixes itself.
    /// </remarks>
    public static MetalRecord? Read(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);

        try
        {
            if (!File.Exists(home.MetalRecordPath))
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(home.MetalRecordPath)) is not JsonObject body)
            {
                return null;
            }

            var lines = new List<string>();
            if (body["lines"] is JsonArray captured)
            {
                foreach (var line in captured)
                {
                    if (line?.GetValue<string>() is { } text)
                    {
                        lines.Add(text);
                    }
                }
            }

            return new MetalRecord(
                body["observed_at"]?.GetValue<string>(),
                body["loader"]?.GetValue<string>(),
                body["os"]?.GetValue<string>(),
                lines,
                body["has_tensor"]?.GetValue<bool>(),
                body["gpu"]?.GetValue<string>());
        }
#pragma warning disable CA1031 // Anything unreadable here is a file the next load replaces.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static bool? ParseHasTensor(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var at = line.IndexOf(TensorNeedle, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var separator = line.IndexOf('=', at);
            if (separator < 0)
            {
                continue;
            }

            return line[(separator + 1)..].Trim().Equals("true", StringComparison.Ordinal);
        }

        return null;
    }

    private static string? ParseGpu(IReadOnlyList<string> lines)
    {
        foreach (var needle in DeviceNeedles)
        {
            foreach (var line in lines)
            {
                var at = line.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                var name = line[(at + needle.Length)..].Trim();
                if (name.Length > 0)
                {
                    return name;
                }
            }
        }

        return null;
    }
}
