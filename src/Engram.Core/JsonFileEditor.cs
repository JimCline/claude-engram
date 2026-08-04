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

    public readonly record struct FileFreshness(bool Exists, DateTime LastWriteTimeUtc, long Length)
    {
        public static FileFreshness Capture(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileFreshness(true, info.LastWriteTimeUtc, info.Length)
                : new FileFreshness(false, default, 0);
        }

        public bool IsUnchanged(string path) => this == Capture(path);
    }

    private const int MaxBackupAttempts = 1000;

    public static string CreateBackup(string path)
    {
        var content = File.ReadAllBytes(path);

        for (var n = 1; n <= MaxBackupAttempts; n++)
        {
            var candidate = $"{path}{EngramHome.DirectoryName}-backup-{n}";
            try
            {
                using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(content, 0, content.Length);
                return candidate;
            }
            catch (IOException)
            {
            }
        }

        throw new IOException($"could not claim a backup filename for '{path}' after {MaxBackupAttempts} concurrent attempts");
    }

    public static string? WriteWithBackup(string path, string content)
    {
        var backupPath = File.Exists(path) ? CreateBackup(path) : null;
        AtomicFile.Write(path, content);
        return backupPath;
    }
}
