using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>
/// Talks to anything exposing <c>POST /v1/embeddings</c>.
/// </summary>
/// <remarks>
/// <para>One client covers LM Studio, llama.cpp's own server, vLLM, OpenAI, Voyage, and most
/// hosted providers, because they all settled on the same request and response shape. That is
/// the whole reason this is worth building before a local runtime: an instance can have working
/// embeddings today against a model the user already has running, with nothing downloaded.</para>
///
/// <para><b>Results are reordered by <c>index</c>, not trusted in array order.</b> The response
/// schema carries an index per embedding precisely because the server may return them out of
/// order, and a provider that batches internally often does. Reading them positionally attaches
/// vectors to the wrong facts — silently, since every vector is individually valid.</para>
///
/// <para><c>encoding_format</c> is sent explicitly. Left unset, some providers answer with
/// base64-packed floats, which parses as a string where a number array was expected and fails
/// the whole batch for no reason a user could diagnose.</para>
/// </remarks>
public sealed class OpenAiCompatibleEmbedder : HttpEmbedder
{
    private readonly string? apiKey;

    public OpenAiCompatibleEmbedder(
        EmbeddingSpace space,
        Uri endpoint,
        TimeSpan timeout,
        string? apiKey = null,
        HttpClient? client = null)
        : base(space, endpoint, timeout, client)
    {
        this.apiKey = apiKey;
        RequestUri = Combine(endpoint, "/embeddings");
    }

    protected override Uri RequestUri { get; }

    protected override JsonNode BuildRequest(IReadOnlyList<string> texts) => new JsonObject
    {
        ["model"] = Space.Model,
        ["input"] = ToArray(texts),
        ["encoding_format"] = "float",
    };

    protected override IReadOnlyList<float[]?>? ReadResponse(JsonNode? body, int expected)
    {
        if (body?["data"] is not JsonArray data || data.Count != expected)
        {
            return null;
        }

        var vectors = new float[]?[expected];
        var filled = 0;
        foreach (var entry in data)
        {
            if (entry?["index"] is not JsonValue indexValue
                || !indexValue.TryGetValue<int>(out var index)
                || index < 0
                || index >= expected
                || vectors[index] is not null)
            {
                return null;
            }

            vectors[index] = ReadVector(entry["embedding"]);
            filled++;
        }

        return filled == expected ? vectors : null;
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        if (apiKey is { Length: > 0 })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    internal static JsonArray ToArray(IReadOnlyList<string> texts)
    {
        var array = new JsonArray();
        // JsonArray.Add(T) binds to the generic, AOT-hostile overload; the IList<JsonNode?>
        // interface reaches the one that takes a node.
        var items = (IList<JsonNode?>)array;
        foreach (var text in texts)
        {
            items.Add(JsonValue.Create(text));
        }

        return array;
    }
}
