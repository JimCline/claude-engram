using System.Text.Json;

namespace Engram.Core;

public sealed class ConfigShapeException : Exception
{
    public string KeyPath { get; }
    public JsonValueKind ActualNodeKind { get; }

    public ConfigShapeException(string keyPath, JsonValueKind actualNodeKind)
        : base($"unexpected JSON shape at '{keyPath}' (found {actualNodeKind})")
    {
        KeyPath = keyPath;
        ActualNodeKind = actualNodeKind;
    }
}
