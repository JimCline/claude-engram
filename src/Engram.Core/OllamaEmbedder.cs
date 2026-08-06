using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>
/// Ollama's native <c>POST /api/embed</c>.
/// </summary>
/// <remarks>
/// Ollama also exposes an OpenAI-compatible route, so this class exists for one reason worth the
/// duplication: the native endpoint takes a list and returns a list, while going through the
/// compatibility shim historically meant one text per request. For a backfill that measures its
/// work in batches, that is the difference between one round trip and sixteen.
///
/// <para>Its response is positional — there is no index field — so unlike the OpenAI shape there
/// is nothing to reorder by, and the count check is the only guard available that the answer
/// lines up with the question.</para>
/// </remarks>
public sealed class OllamaEmbedder : HttpEmbedder
{
    public OllamaEmbedder(
        EmbeddingSpace space,
        Uri endpoint,
        TimeSpan timeout,
        HttpClient? client = null)
        : base(space, endpoint, timeout, client)
    {
        RequestUri = Combine(endpoint, "/api/embed");
    }

    protected override Uri RequestUri { get; }

    protected override JsonNode BuildRequest(IReadOnlyList<string> texts) => new JsonObject
    {
        ["model"] = Space.Model,
        ["input"] = OpenAiCompatibleEmbedder.ToArray(texts),
    };

    protected override IReadOnlyList<float[]?>? ReadResponse(JsonNode? body, int expected)
    {
        if (body?["embeddings"] is not JsonArray embeddings || embeddings.Count != expected)
        {
            return null;
        }

        var vectors = new float[]?[expected];
        for (var i = 0; i < expected; i++)
        {
            vectors[i] = ReadVector(embeddings[i]);
        }

        return vectors;
    }
}
