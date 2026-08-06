using System.Net;
using System.Text;
using Engram.Core;

namespace Engram.Core.Tests;

public sealed class EmbeddingProbeTests
{
    private sealed class FakeHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<Uri> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!);
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return respond(Requests[^1]);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string Vector(int width) => "[" + string.Join(',', Enumerable.Repeat("0.1", width)) + "]";

    private static EmbeddingSettings Endpoint(
        EmbeddingProvider provider = EmbeddingProvider.OpenAiCompatible,
        string? model = "some-embed",
        int? dimensions = null,
        string? endpoint = "http://localhost:1234/v1") =>
        EmbeddingSettings.Disabled with
        {
            Provider = provider,
            Model = model,
            Endpoint = endpoint,
            Dimensions = dimensions,
        };

    private static ProbeResult Run(EmbeddingSettings settings, FakeHandler handler) =>
        EmbeddingProbe.Run(settings, _ => null, new HttpClient(handler));

    [Fact]
    public void AnEndpointThatAnswers_ReportsTheWidthItReturned()
    {
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(1024)}}}]}"""));

        var result = Run(Endpoint(), handler);

        Assert.True(result.Answered);
        Assert.Equal(1024, result.Dimensions);
    }

    [Fact]
    public void TheProbe_SendsExactlyOneRequest()
    {
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(768)}}}]}"""));

        Run(Endpoint(), handler);

        Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:1234/v1/embeddings", handler.Urls[0].AbsoluteUri);
    }

    [Fact]
    public void TheProbe_NamesTheModelItWasAskedAbout()
    {
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(768)}}}]}"""));

        Run(Endpoint(model: "bge-m3"), handler);

        Assert.Contains("\"bge-m3\"", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AWidthUnlikeTheOneInTheConfig_IsStillReported()
    {
        // The whole point. The embedder refuses a mismatch on the write path, correctly; the probe
        // has to be able to see one, or it can never tell anybody their dim is wrong.
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(1024)}}}]}"""));

        var result = Run(Endpoint(dimensions: 384), handler);

        Assert.Equal(1024, result.Dimensions);
    }

    [Fact]
    public void AConfigWithNoWidthAtAll_IsProbedRatherThanRefused()
    {
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(512)}}}]}"""));

        var result = Run(Endpoint(dimensions: null), handler);

        Assert.Equal(512, result.Dimensions);
    }

    [Fact]
    public void AConfigCarryingComplaints_IsProbedAnyway()
    {
        // Those complaints are about the very thing being measured.
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(768)}}}]}"""));
        var settings = Endpoint() with { Problems = ["[embedding] dim is required."] };

        Assert.Equal(768, Run(settings, handler).Dimensions);
    }

    [Fact]
    public void Ollama_IsAskedOnItsOwnEndpoint()
    {
        var handler = new FakeHandler(_ => Json($$"""{"embeddings":[{{Vector(768)}}]}"""));

        var result = Run(
            Endpoint(EmbeddingProvider.Ollama, endpoint: "http://localhost:11434"),
            handler);

        Assert.Equal(768, result.Dimensions);
        Assert.Equal("http://localhost:11434/api/embed", handler.Urls[0].AbsoluteUri);
    }

    [Fact]
    public void AnEndpointThatRefuses_SaysSoRatherThanGuessing()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = Run(Endpoint(), handler);

        Assert.False(result.Answered);
        Assert.Null(result.Dimensions);
        Assert.Contains("did not answer", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndpointReturningNonsense_SaysSoRatherThanGuessing()
    {
        var handler = new FakeHandler(_ => Json("""{"nothing":"useful"}"""));

        Assert.False(Run(Endpoint(), handler).Answered);
    }

    [Fact]
    public void AnUnreachableEndpoint_SaysSoRatherThanThrowing()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("refused"));

        var result = Run(Endpoint(), handler);

        Assert.False(result.Answered);
        Assert.Contains("did not answer", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoEndpointSet_NothingIsAsked()
    {
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(768)}}}]}"""));

        var result = Run(Endpoint(endpoint: null), handler);

        Assert.False(result.Answered);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void WithNoModelNamed_SaysItIsTheModelThatIsMissing()
    {
        // Nothing would be sent either way — the factory has its own reason to refuse. What this
        // guards is which reason the user is given: the factory's mentions the missing dim, which
        // during a probe is the question rather than the fault.
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(768)}}}]}"""));

        var result = Run(Endpoint(model: null), handler);

        Assert.False(result.Answered);
        Assert.Empty(handler.Requests);
        Assert.Contains("model is not set", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("dim", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithEmbeddingsOff_NothingIsAsked()
    {
        var handler = new FakeHandler(_ => Json($$"""{"data":[{"index":0,"embedding":{{Vector(768)}}}]}"""));

        var result = EmbeddingProbe.Run(EmbeddingSettings.Disabled, _ => null, new HttpClient(handler));

        Assert.False(result.Answered);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ALocalModel_IsAnsweredFromWhatEngramAlreadyKnows()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request should be sent"));
        var known = EmbeddingModels.All[0];

        var result = Run(Endpoint(EmbeddingProvider.Local, model: known.Id, endpoint: null), handler);

        Assert.Equal(known.Dimensions, result.Dimensions);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AnUnknownLocalModel_HasNoWidthToReport()
    {
        var handler = new FakeHandler(_ => Json("{}"));

        var result = Run(Endpoint(EmbeddingProvider.Local, model: "not-a-model", endpoint: null), handler);

        Assert.False(result.Answered);
        Assert.Contains("not one Engram knows", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ForProbe_SuppliesAWidthWithoutInventingOne()
    {
        var provisional = EmbeddingProbe.ForProbe(Endpoint(dimensions: null));

        Assert.Equal(EmbeddingProbe.ProvisionalWidth, provisional.Dimensions);
        Assert.NotNull(provisional.Space);
    }

    [Fact]
    public void ForProbe_KeepsAWidthThatIsAlreadyThere()
    {
        Assert.Equal(384, EmbeddingProbe.ForProbe(Endpoint(dimensions: 384)).Dimensions);
    }
}
