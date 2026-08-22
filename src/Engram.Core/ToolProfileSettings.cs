namespace Engram.Core;

/// <summary>Which MCP tools a server connection advertises (docs/memory-expansion/03-tool-profiles-spec.md).</summary>
public enum ToolProfile
{
    /// <summary>Everything except the lifecycle tools (start/status/stop) — the everyday set.</summary>
    Default,

    /// <summary>Every tool, including lifecycle.</summary>
    Full,
}

/// <summary>
/// The <c>[mcp]</c> section: which profile a server connection advertises tools under.
/// </summary>
/// <remarks>
/// Mirrors <see cref="MemorySettings"/>'s shape — a config-backed enum with a default that a
/// malformed value falls back to, plus a <see cref="Problems"/> list <c>doctor</c> can report
/// (D37: reads, never repairs). The profile itself does not gate the server's behaviour beyond
/// which tool types it registers; see <c>ToolProfiles</c> for the tool-name mapping.
/// </remarks>
public sealed record ToolProfileSettings(ToolProfile Profile, IReadOnlyList<string> Problems)
{
    public const string Section = "mcp";

    public const string Key = "tool_profile";

    public const ToolProfile DefaultProfile = ToolProfile.Default;

    public static ToolProfileSettings Default { get; } = new(DefaultProfile, []);

    /// <summary>The names accepted in the config file and on the command line, in reporting order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["default", "full"];

    public static ToolProfileSettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.String(Section, Key) is not { } text)
        {
            return Default;
        }

        if (!TryParse(text, out var profile))
        {
            return new ToolProfileSettings(
                DefaultProfile,
                [$"[{Section}] {Key} is '{text}', which is not one of {string.Join(", ", Names)}; using {ToText(DefaultProfile)}."]);
        }

        return new ToolProfileSettings(profile, []);
    }

    public static bool TryParse(string? text, out ToolProfile profile)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "default":
                profile = ToolProfile.Default;
                return true;
            case "full":
                profile = ToolProfile.Full;
                return true;
            default:
                profile = DefaultProfile;
                return false;
        }
    }

    public static string ToText(ToolProfile profile) => profile switch
    {
        ToolProfile.Default => "default",
        ToolProfile.Full => "full",
        _ => "default",
    };
}
