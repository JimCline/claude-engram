using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>
/// Thrown when the settings file exists but is not something we are willing to rewrite.
/// </summary>
public sealed class ClaudeSettingsException(string message) : Exception(message);

public sealed record PermissionGrantPlan(
    string SettingsPath,
    IReadOnlyList<string> ToAdd,
    IReadOnlyList<string> AlreadyPresent,
    bool SettingsFileExisted);

public sealed record PermissionRevokePlan(
    string SettingsPath,
    IReadOnlyList<string> ToRemove,
    IReadOnlyList<string> LeftAlone);

/// <summary>
/// Adds Engram's MCP tools to Claude Code's <c>permissions.allow</c> list so the agent can
/// reach memory without a confirmation prompt on every call.
///
/// This exists because the prompt is not merely an annoyance: M0 measures whether the model
/// reaches for memory at all, and a permission dialog in front of every recall makes that
/// number a measurement of the dialog instead. Approval fatigue and tool avoidance both push
/// the same direction, and neither is the thing under test.
///
/// Three tools are deliberately left out of the grant.
/// <list type="bullet">
/// <item><c>engram_forget</c> closes a fact, and there is no un-retract — the one call where a
/// human in the loop is worth the interruption.</item>
/// <item><c>engram_start</c> and <c>engram_stop</c> move the daemon out from under the session
/// that is talking to it.</item>
/// </list>
/// A server-wide wildcard would be one line instead of four, and would silently pull all three
/// back in the moment any of them ships.
/// </summary>
public static class ClaudePermissions
{
    /// <summary>
    /// The tool names as Claude Code namespaces them for a plugin-provided MCP server:
    /// <c>mcp__plugin_&lt;plugin&gt;_&lt;server&gt;__&lt;tool&gt;</c>. Both halves come from the
    /// plugin itself rather than from the marketplace it was installed through, so these strings
    /// survive a user adding the same plugin under a different marketplace name.
    /// </summary>
    public static readonly IReadOnlyList<string> GrantedTools =
    [
        "mcp__plugin_engram_engram__engram_recall",
        "mcp__plugin_engram_engram__engram_remember",
        "mcp__plugin_engram_engram__engram_digest",
        "mcp__plugin_engram_engram__engram_status",
    ];

    public static PermissionGrantPlan PlanGrant(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        var existed = File.Exists(settingsPath);
        var present = existed ? ReadAllowList(settingsPath) : new HashSet<string>(StringComparer.Ordinal);

        var toAdd = new List<string>();
        var alreadyPresent = new List<string>();

        foreach (var tool in GrantedTools)
        {
            if (present.Contains(tool))
            {
                alreadyPresent.Add(tool);
            }
            else
            {
                toAdd.Add(tool);
            }
        }

        return new PermissionGrantPlan(settingsPath, toAdd, alreadyPresent, existed);
    }

    /// <summary>
    /// Writes the grant and records what it added. The record is what makes removal safe later:
    /// an entry the user wrote themselves is never recorded, so the uninstaller can only ever
    /// take back lines this code put there.
    /// </summary>
    public static void ApplyGrant(PermissionGrantPlan plan, string recordPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);

        if (plan.ToAdd.Count == 0)
        {
            return;
        }

        var root = plan.SettingsFileExisted ? ParseSettings(plan.SettingsPath) : new JsonObject();
        var allow = EnsureAllowArray(root);

        foreach (var tool in plan.ToAdd)
        {
            ((IList<JsonNode?>)allow).Add(JsonValue.Create(tool));
        }

        AtomicFile.Write(plan.SettingsPath, Serialize(root));

        var recorded = ReadRecord(recordPath);
        foreach (var tool in plan.ToAdd)
        {
            recorded.Add(tool);
        }

        WriteRecord(recordPath, recorded);
    }

    public static PermissionRevokePlan PlanRevoke(string settingsPath, string recordPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);

        if (!File.Exists(settingsPath))
        {
            return new PermissionRevokePlan(settingsPath, [], []);
        }

        var present = ReadAllowList(settingsPath);
        var ours = ReadRecord(recordPath);

        var toRemove = new List<string>();
        var leftAlone = new List<string>();

        foreach (var tool in GrantedTools)
        {
            if (!present.Contains(tool))
            {
                continue;
            }

            if (ours.Contains(tool))
            {
                toRemove.Add(tool);
            }
            else
            {
                leftAlone.Add(tool);
            }
        }

        return new PermissionRevokePlan(settingsPath, toRemove, leftAlone);
    }

    public static void ApplyRevoke(PermissionRevokePlan plan, string recordPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);

        if (plan.ToRemove.Count == 0)
        {
            return;
        }

        var root = ParseSettings(plan.SettingsPath);
        var allow = EnsureAllowArray(root);
        var doomed = new HashSet<string>(plan.ToRemove, StringComparer.Ordinal);

        var list = (IList<JsonNode?>)allow;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && doomed.Contains(text))
            {
                list.RemoveAt(i);
            }
        }

        AtomicFile.Write(plan.SettingsPath, Serialize(root));

        var recorded = ReadRecord(recordPath);
        recorded.ExceptWith(doomed);
        WriteRecord(recordPath, recorded);
    }

    // The suffix has to match the one install.sh already uses for shell startup files, so a user
    // who goes looking for what the installer backed up finds one convention, not two.
    public static string BackupPath(string settingsPath, DateTime utcNow) =>
        $"{settingsPath}.engram-backup-{utcNow:yyyyMMdd'T'HHmmss'Z'}"; // engram-lint:allow(backup filename suffix, not a home path)

    private static HashSet<string> ReadAllowList(string settingsPath)
    {
        var root = ParseSettings(settingsPath);
        var present = new HashSet<string>(StringComparer.Ordinal);

        if (root["permissions"] is not JsonObject permissions || permissions["allow"] is not JsonArray allow)
        {
            return present;
        }

        foreach (var entry in allow)
        {
            if (entry is JsonValue value && value.TryGetValue<string>(out var text))
            {
                present.Add(text);
            }
        }

        return present;
    }

    /// <summary>
    /// Parses strictly, and refuses anything it cannot round-trip. Comments and trailing commas
    /// would parse under relaxed options and then vanish on the way back out, which is a silent
    /// edit to a file the user hand-maintains — worse than declining to help.
    /// </summary>
    private static JsonObject ParseSettings(string settingsPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(settingsPath);
        }
        catch (IOException ex)
        {
            throw new ClaudeSettingsException($"could not read {settingsPath}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ClaudeSettingsException(
                $"{settingsPath} is not strict JSON ({ex.Message}). Refusing to rewrite it, "
                    + "because reformatting would drop comments or trailing commas without saying so.");
        }

        return parsed as JsonObject
            ?? throw new ClaudeSettingsException($"{settingsPath} does not contain a JSON object at its root.");
    }

    private static JsonArray EnsureAllowArray(JsonObject root)
    {
        if (root["permissions"] is not JsonObject permissions)
        {
            permissions = [];
            root["permissions"] = permissions;
        }

        if (permissions["allow"] is not JsonArray allow)
        {
            allow = [];
            permissions["allow"] = allow;
        }

        return allow;
    }

    private static string Serialize(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static HashSet<string> ReadRecord(string recordPath)
    {
        var recorded = new HashSet<string>(StringComparer.Ordinal);

        if (!File.Exists(recordPath))
        {
            return recorded;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(recordPath));
        }
        catch (JsonException)
        {
            return recorded;
        }

        if (parsed is not JsonArray entries)
        {
            return recorded;
        }

        foreach (var entry in entries)
        {
            if (entry is JsonValue value && value.TryGetValue<string>(out var text))
            {
                recorded.Add(text);
            }
        }

        return recorded;
    }

    private static void WriteRecord(string recordPath, HashSet<string> tools)
    {
        if (tools.Count == 0)
        {
            if (File.Exists(recordPath))
            {
                File.Delete(recordPath);
            }

            return;
        }

        var array = new JsonArray();
        var list = (IList<JsonNode?>)array;
        foreach (var tool in tools.OrderBy(t => t, StringComparer.Ordinal))
        {
            list.Add(JsonValue.Create(tool));
        }

        AtomicFile.Write(recordPath, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
