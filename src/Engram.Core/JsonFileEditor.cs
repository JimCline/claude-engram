using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.Core;

public static class JsonFileEditor
{
    public static bool TryReadObject(string path, out JsonObject result, out string? error)
    {
        if (!File.Exists(path))
        {
            result = [];
            error = null;
            return true;
        }

        try
        {
            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text);
            if (node is not JsonObject obj)
            {
                throw new FormatException("Top-level JSON value must be an object.");
            }

            result = obj;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            result = [];
            error = ex.Message;
            return false;
        }
    }

    public static string ToIndentedJson(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    public static string NextBackupPath(string path)
    {
        for (var n = 1; ; n++)
        {
            var candidate = $"{path}{EngramHome.DirectoryName}-backup-{n}";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    public static string? WriteWithBackup(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string? backupPath = null;
        if (File.Exists(path))
        {
            backupPath = NextBackupPath(path);
            File.Copy(path, backupPath);
        }

        File.WriteAllText(path, content);
        return backupPath;
    }
}
