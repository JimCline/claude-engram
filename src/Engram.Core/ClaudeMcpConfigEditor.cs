using System.Text.Json.Nodes;

namespace Engram.Core;

public static class ClaudeMcpConfigEditor
{
    private const string EngramServerKey = "engram";

    public static JsonObject ApplyInstall(JsonObject config, string engramBinaryPath)
    {
        var servers = GetOrAddObject(config, "mcpServers");
        servers[EngramServerKey] = new JsonObject
        {
            ["command"] = engramBinaryPath,
            ["args"] = new JsonArray(JsonValue.Create("mcp")),
        };

        return config;
    }

    public static JsonObject ApplyUninstall(JsonObject config)
    {
        if (config["mcpServers"] is JsonObject servers)
        {
            servers.Remove(EngramServerKey);

            if (servers.Count == 0)
            {
                config.Remove("mcpServers");
            }
        }

        return config;
    }

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
