using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Engram.EndToEnd.Tests;

internal sealed class HttpMcpClient(int port) : IDisposable
{
    private readonly HttpClient _client = new();
    private readonly Uri _endpoint = new($"http://127.0.0.1:{port}/");
    private int _nextId = 1;

    public string? SessionId { get; private set; }

    public async Task<IReadOnlyDictionary<string, string[]>> InitializeAsync(CancellationToken cancellationToken)
    {
        var (_, headers) = await SendRawAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = _nextId++,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "engram-e2e-test", ["version"] = "0.0.1" },
                },
            },
            cancellationToken);

        await SendRawAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, cancellationToken);

        return headers;
    }

    public async Task<JsonNode?> ListToolsAsync(CancellationToken cancellationToken)
    {
        var (body, _) = await SendRawAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = _nextId++, ["method"] = "tools/list", ["params"] = new JsonObject() },
            cancellationToken);
        return ExtractResult(body);
    }

    public async Task<string> CallToolTextAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        var (body, _) = await SendRawAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = _nextId++,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
            },
            cancellationToken);

        var result = ExtractResult(body);
        var content = result!["result"]!["content"]!.AsArray();
        var sb = new StringBuilder();
        foreach (var block in content)
        {
            sb.Append(block!["text"]!.GetValue<string>());
        }

        return sb.ToString();
    }

    public async Task<IReadOnlyDictionary<string, string[]>> ListToolsHeadersAsync(CancellationToken cancellationToken)
    {
        var (_, headers) = await SendRawAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = _nextId++, ["method"] = "tools/list", ["params"] = new JsonObject() },
            cancellationToken);
        return headers;
    }

    private static JsonNode? ExtractResult(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return JsonNode.Parse(line["data: ".Length..]);
            }
        }

        return JsonNode.Parse(body);
    }

    private async Task<(string Body, IReadOnlyDictionary<string, string[]> Headers)> SendRawAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (SessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", SessionId);
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            SessionId = values.First();
        }

        var headerSnapshot = response.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (body, headerSnapshot);
    }

    public void Dispose() => _client.Dispose();
}
