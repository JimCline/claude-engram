using System.Text.Json.Nodes;

namespace Engram.Core;

public static class ClaudeCodeSettingsEditor
{
    private const string EngramHookMarker = "engram hook ";

    private static readonly string[] EngramHookEvents =
    [
        "session-start",
        "pre-compact",
        "file-touched",
    ];

    public static JsonObject ApplyInstall(JsonObject settings, string engramBinaryPath)
    {
        var hooks = GetOrAddObject(settings, "hooks");

        UpsertHook(hooks, "SessionStart", matcher: null, command: $"{engramBinaryPath} hook session-start");
        UpsertHook(hooks, "PreCompact", matcher: null, command: $"{engramBinaryPath} hook pre-compact");
        UpsertHook(hooks, "PostToolUse", matcher: "Edit|Write|MultiEdit|NotebookEdit", command: $"{engramBinaryPath} hook file-touched");

        return settings;
    }

    public static JsonObject ApplyUninstall(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks)
        {
            return settings;
        }

        foreach (var eventName in hooks.Select(kvp => kvp.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray groups)
            {
                continue;
            }

            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i] is not JsonObject group || group["hooks"] is not JsonArray hookEntries)
                {
                    continue;
                }

                RemoveEngramEntries(hookEntries);

                if (hookEntries.Count == 0)
                {
                    groups.RemoveAt(i);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        if (hooks.Count == 0)
        {
            settings.Remove("hooks");
        }

        return settings;
    }

    private static void UpsertHook(JsonObject hooks, string eventName, string? matcher, string command)
    {
        var existingEventNode = hooks[eventName];
        if (existingEventNode is not null && existingEventNode is not JsonArray)
        {
            throw new ConfigShapeException($"hooks.{eventName}", existingEventNode.GetValueKind());
        }

        if (hooks[eventName] is not JsonArray groups)
        {
            groups = [];
            hooks[eventName] = groups;
        }

        var existingGroup = groups
            .OfType<JsonObject>()
            .FirstOrDefault(group => group["hooks"] is JsonArray entries && ContainsEngramEntry(entries));

        if (existingGroup is not null)
        {
            var entries = (JsonArray)existingGroup["hooks"]!;
            RemoveEngramEntries(entries);
            ((IList<JsonNode?>)entries).Add(NewHookEntry(command));

            if (matcher is not null)
            {
                existingGroup["matcher"] = matcher;
            }
            else
            {
                existingGroup.Remove("matcher");
            }

            return;
        }

        var newGroup = new JsonObject();
        if (matcher is not null)
        {
            newGroup["matcher"] = matcher;
        }

        newGroup["hooks"] = new JsonArray(NewHookEntry(command));
        ((IList<JsonNode?>)groups).Add(newGroup);
    }

    private static JsonObject NewHookEntry(string command) =>
        new() { ["type"] = "command", ["command"] = command };

    private static bool ContainsEngramEntry(JsonArray entries) =>
        entries.OfType<JsonObject>().Any(IsEngramEntry);

    private static void RemoveEngramEntries(JsonArray entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is JsonObject entry && IsEngramEntry(entry))
            {
                entries.RemoveAt(i);
            }
        }
    }

    private static bool IsEngramEntry(JsonObject entry) =>
        entry["command"] is JsonValue value &&
        value.TryGetValue<string>(out var command) &&
        command.Contains(EngramHookMarker, StringComparison.Ordinal) &&
        EngramHookEvents.Any(eventName => command.EndsWith(eventName, StringComparison.Ordinal));

    private static JsonObject GetOrAddObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        if (parent[key] is JsonNode existingNode)
        {
            throw new ConfigShapeException(key, existingNode.GetValueKind());
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }
}
