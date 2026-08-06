using System.Net;
using System.Text;
using Engram.Core;

namespace Engram.Core.Tests;

public class HttpEmbedderTests
{
    private static readonly EmbeddingSpace Space = new("test-model", 3);
    private static readonly Uri Endpoint = new("http://localhost:1234/v1");

    private sealed class FakeHandler(Func<string, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<Uri> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!);
            Headers = request.Headers;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(body);
            return respond(body, Requests.Count - 1);
        }

        public System.Net.Http.Headers.HttpRequestHeaders? Headers { get; private set; }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    /// <summary>A vector that arrived, stated as an assertion rather than a suppression.</summary>
    private static float[] NotNull(float[]? vector)
    {
        Assert.NotNull(vector);
        return vector;
    }

    private static (OpenAiCompatibleEmbedder Embedder, FakeHandler Handler) OpenAi(
        Func<string, int, HttpResponseMessage> respond,
        string? apiKey = null,
        Uri? endpoint = null)
    {
        var handler = new FakeHandler(respond);
        var embedder = new OpenAiCompatibleEmbedder(
            Space, endpoint ?? Endpoint, TimeSpan.FromSeconds(5), apiKey, new HttpClient(handler));
        return (embedder, handler);
    }

    [Fact]
    public async Task OpenAi_ReturnsOneVectorPerInput()
    {
        var (embedder, handler) = OpenAi((_, _) => Json(
            """
            {"data":[{"index":0,"embedding":[1,2,3]},{"index":1,"embedding":[4,5,6]}]}
            """));
        using var _ = embedder;

        var vectors = await embedder.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal([1f, 2f, 3f], NotNull(vectors[0]));
        Assert.Equal([4f, 5f, 6f], NotNull(vectors[1]));
        Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:1234/v1/embeddings", handler.Urls[0].AbsoluteUri);
    }

    /// <summary>
    /// The response carries an index per embedding precisely because the server may answer out
    /// of order. Reading positionally attaches vectors to the wrong facts, and every vector is
    /// individually valid, so nothing downstream can notice.
    /// </summary>
    [Fact]
    public async Task OpenAi_ReordersByIndexRatherThanTrustingArrayOrder()
    {
        var (embedder, _) = OpenAi((_, _) => Json(
            """
            {"data":[{"index":1,"embedding":[4,5,6]},{"index":0,"embedding":[1,2,3]}]}
            """));
        using var _e = embedder;

        var vectors = await embedder.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal([1f, 2f, 3f], NotNull(vectors[0]));
        Assert.Equal([4f, 5f, 6f], NotNull(vectors[1]));
    }

    [Fact]
    public async Task OpenAi_WithARepeatedIndex_TreatsTheBatchAsFailed()
    {
        var (embedder, handler) = OpenAi((_, call) => call == 0
            ? Json("""{"data":[{"index":0,"embedding":[1,2,3]},{"index":0,"embedding":[4,5,6]}]}""")
            : Json("""{"data":[{"index":0,"embedding":[9,9,9]}]}"""));
        using var _ = embedder;

        var vectors = await embedder.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        // Two vectors claiming to be input 0 means one input has no answer. Falling back is the
        // only reading that cannot silently mislabel.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([9f, 9f, 9f], NotNull(vectors[0]));
        Assert.Equal([9f, 9f, 9f], NotNull(vectors[1]));
    }

    /// <summary>
    /// A failed batch says nothing about which input broke it, and the queue is ordered — so a
    /// batch that always fails would block every fact behind it forever.
    /// </summary>
    [Fact]
    public async Task OpenAi_WhenABatchFails_RetriesEachTextAlone()
    {
        var (embedder, handler) = OpenAi((body, call) =>
        {
            if (call == 0)
            {
                return Status(HttpStatusCode.InternalServerError);
            }

            return body.Contains("poison", StringComparison.Ordinal)
                ? Status(HttpStatusCode.BadRequest)
                : Json("""{"data":[{"index":0,"embedding":[7,7,7]}]}""");
        });
        using var _ = embedder;

        var vectors = await embedder.EmbedAsync(
            ["fine", "poison", "also fine"], TestContext.Current.CancellationToken);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([7f, 7f, 7f], NotNull(vectors[0]));
        Assert.Null(vectors[1]);
        Assert.Equal([7f, 7f, 7f], NotNull(vectors[2]));
    }

    [Fact]
    public async Task OpenAi_WhenTheTransportFails_ReturnsNullsRatherThanThrowing()
    {
        var (embedder, _) = OpenAi((_, _) => throw new HttpRequestException("connection refused"));
        using var _e = embedder;

        var vectors = await embedder.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(vectors));
    }

    /// <summary>
    /// A width mismatch is a configuration fault affecting every text equally. Reporting it as a
    /// poison batch would hide the one thing worth saying, and storing it would corrupt every
    /// later query without erroring anywhere.
    /// </summary>
    [Fact]
    public async Task OpenAi_WithTheWrongWidth_Throws()
    {
        var (embedder, _) = OpenAi((_, _) => Json("""{"data":[{"index":0,"embedding":[1,2,3,4,5]}]}"""));
        using var _e = embedder;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => embedder.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Contains("3 dimensions", error.Message, StringComparison.Ordinal);
        Assert.Contains("returned 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_SendsBearerAuthOnlyWhenGivenAKey()
    {
        var (withKey, keyed) = OpenAi(
            (_, _) => Json("""{"data":[{"index":0,"embedding":[1,2,3]}]}"""), apiKey: "sk-test");
        using (withKey)
        {
            await withKey.EmbedAsync(["a"], TestContext.Current.CancellationToken);
            Assert.Equal("Bearer", keyed.Headers!.Authorization!.Scheme);
            Assert.Equal("sk-test", keyed.Headers.Authorization.Parameter);
        }

        var (without, bare) = OpenAi((_, _) => Json("""{"data":[{"index":0,"embedding":[1,2,3]}]}"""));
        using (without)
        {
            await without.EmbedAsync(["a"], TestContext.Current.CancellationToken);
            Assert.Null(bare.Headers!.Authorization);
        }
    }

    [Fact]
    public async Task OpenAi_AsksForFloatEncodingExplicitly()
    {
        // Left unset, some providers answer with base64-packed floats, which parses as a string
        // where a number array was expected and fails the batch for no diagnosable reason.
        var (embedder, handler) = OpenAi((_, _) => Json("""{"data":[{"index":0,"embedding":[1,2,3]}]}"""));
        using var _ = embedder;

        await embedder.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Contains("\"encoding_format\":\"float\"", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_WithAFullPathEndpoint_DoesNotDoubleTheSuffix()
    {
        var (embedder, handler) = OpenAi(
            (_, _) => Json("""{"data":[{"index":0,"embedding":[1,2,3]}]}"""),
            endpoint: new Uri("https://api.example.com/v1/embeddings"));
        using var _ = embedder;

        await embedder.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Equal("https://api.example.com/v1/embeddings", handler.Urls[0].AbsoluteUri);
    }

    [Fact]
    public async Task EmptyInput_MakesNoRequest()
    {
        var (embedder, handler) = OpenAi((_, _) => throw new InvalidOperationException("should not be called"));
        using var _ = embedder;

        Assert.Empty(await embedder.EmbedAsync([], TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Ollama_ReadsThePositionalBatchShape()
    {
        var handler = new FakeHandler((_, _) => Json("""{"embeddings":[[1,2,3],[4,5,6]]}"""));
        using var embedder = new OllamaEmbedder(
            Space, new Uri("http://localhost:11434"), TimeSpan.FromSeconds(5), new HttpClient(handler));

        var vectors = await embedder.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal([1f, 2f, 3f], NotNull(vectors[0]));
        Assert.Equal([4f, 5f, 6f], NotNull(vectors[1]));
        Assert.Equal("http://localhost:11434/api/embed", handler.Urls[0].AbsoluteUri);
    }

    /// <summary>
    /// Ollama's response has no index, so the count is the only available check that the answer
    /// lines up with the question.
    /// </summary>
    [Fact]
    public async Task Ollama_WithAShortResponse_FallsBackToSingleRequests()
    {
        var handler = new FakeHandler((_, call) => call == 0
            ? Json("""{"embeddings":[[1,2,3]]}""")
            : Json("""{"embeddings":[[8,8,8]]}"""));
        using var embedder = new OllamaEmbedder(
            Space, new Uri("http://localhost:11434"), TimeSpan.FromSeconds(5), new HttpClient(handler));

        var vectors = await embedder.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([8f, 8f, 8f], NotNull(vectors[0]));
        Assert.Equal([8f, 8f, 8f], NotNull(vectors[1]));
    }
}
