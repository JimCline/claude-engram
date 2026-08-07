using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engram.Core;

/// <param name="StartTimeUtc">Display metadata, and the comparison for records written before
/// <paramref name="StartToken"/> existed. It identifies nothing once a token is present.</param>
/// <param name="StartToken">
/// What identifies this run of the process (see <see cref="ProcessStartToken"/>). Null means a
/// record written by a binary that predates tokens, which selects the legacy comparison — the two
/// modes are disjoint, and nothing may ever convert between them.
/// </param>
public sealed record PidFileRecord(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("start_time")] DateTimeOffset StartTimeUtc,
    [property: JsonPropertyName("start_token")] string? StartToken = null);

[JsonSerializable(typeof(PidFileRecord))]
internal sealed partial class PidFileJsonContext : JsonSerializerContext;

public static class PidFile
{
    private const string FileName = "engram.pid";

    public static string ResolvePath(EngramHome home) => Path.Combine(home.Root, FileName);

    public static PidFileRecord? Read(EngramHome home)
    {
        var path = ResolvePath(home);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PidFileJsonContext.Default.PidFileRecord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Write(EngramHome home, PidFileRecord record)
    {
        var path = ResolvePath(home);
        var json = JsonSerializer.Serialize(record, PidFileJsonContext.Default.PidFileRecord);
        using var pending = AtomicFile.Prepare(path, json + "\n");
        pending.Commit();

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void Delete(EngramHome home)
    {
        var path = ResolvePath(home);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
