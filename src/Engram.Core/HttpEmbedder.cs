using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Engram.Core;

/// <summary>
/// Shared machinery for embedders that are a POST away: request shaping, the failure ladder,
/// and the serialization every provider behind one model needs whether it asks for it or not.
/// </summary>
/// <remarks>
/// <para><b>Batch, then retry singly, then give up on one text.</b> A batch request that fails
/// says nothing about which input broke it, and the honest response is not to fail all of them
/// — the backfill queue is ordered, so a permanently-failing batch blocks every fact behind it
/// forever. So a failed batch is retried one text at a time, and only a text that fails alone
/// returns null. That costs N+1 requests on the rare bad batch and nothing at all otherwise.</para>
///
/// <para><b>Callers are serialized.</b> Not because HTTP needs it, but because the thing on the
/// other end usually has one model resident and would serialize anyway — worse, without
/// backpressure. Doing it here makes every provider behave the same way, so the pass that
/// drains the queue cannot accidentally depend on a provider that happens to be concurrent.</para>
///
/// <para><b>A wrong width throws rather than returning null.</b> Null means "this one text
/// could not be embedded"; a systematic width mismatch is a configuration fault affecting every
/// text equally, and reporting it as a poison batch would hide the one thing worth saying.</para>
/// </remarks>
public abstract class HttpEmbedder : IEmbedder, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient client;
    private readonly bool ownsClient;

    protected HttpEmbedder(EmbeddingSpace space, Uri endpoint, TimeSpan timeout, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Space = space;
        Endpoint = endpoint;
        ownsClient = client is null;
        this.client = client ?? new HttpClient();
        this.client.Timeout = timeout;
    }

    public EmbeddingSpace Space { get; }

    protected Uri Endpoint { get; }

    protected HttpClient Client => client;

    /// <summary>The URL a batch of texts is POSTed to.</summary>
    protected abstract Uri RequestUri { get; }

    /// <summary>The request body for these texts.</summary>
    protected abstract JsonNode BuildRequest(IReadOnlyList<string> texts);

    /// <summary>
    /// Pulls one vector per input out of the response, in input order, or null if the body is
    /// not the shape this provider promised.
    /// </summary>
    protected abstract IReadOnlyList<float[]?>? ReadResponse(JsonNode? body, int expected);

    public async Task<IReadOnlyList<float[]?>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = await TryEmbedAsync(texts, cancellationToken).ConfigureAwait(false);
            if (batch is not null)
            {
                return batch;
            }

            var one = new string[1];
            var results = new float[]?[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                one[0] = texts[i];
                results[i] = (await TryEmbedAsync(one, cancellationToken).ConfigureAwait(false))?[0];
            }

            return results;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// One round trip. Null means the whole request failed — a transport error, a non-success
    /// status, or a body that did not parse — as distinct from a vector that came back null.
    /// </summary>
    private async Task<IReadOnlyList<float[]?>?> TryEmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var vectors = await SendAsync(texts, cancellationToken).ConfigureAwait(false);
        if (vectors is null)
        {
            return null;
        }

        foreach (var vector in vectors)
        {
            if (vector is not null && vector.Length != Space.Dimensions)
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} is configured for {Space.Dimensions} dimensions but "
                    + $"{Endpoint} returned {vector.Length}. Fix [embedding] dim, or point "
                    + "at a different model — a mismatched width corrupts every query it "
                    + "reaches without erroring anywhere.");
            }
        }

        return vectors;
    }

    /// <summary>Asks the endpoint how wide its vectors are, by embedding one short string.</summary>
    /// <returns>The width, or null if the endpoint could not be reached or did not answer usefully.</returns>
    /// <remarks>
    /// This is the one caller allowed past the width check above, and it has to be: the check
    /// compares against a width that, at probe time, nobody knows yet. That is the entire reason
    /// the probe exists — a hand-typed <c>dim</c> does not fail loudly when it is wrong, it
    /// produces vectors that never match anything. Asking the endpoint replaces a lookup with an
    /// observation. The <see cref="EmbeddingSpace"/> this embedder was built with is a placeholder
    /// here and its width means nothing; only the model name on it is used, because that is what
    /// the request body has to name.
    /// </remarks>
    public async Task<int?> ProbeWidthAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vectors = await SendAsync(["engram probe"], cancellationToken).ConfigureAwait(false);
            return vectors is [{ } vector] ? vector.Length : null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One round trip, with the vectors exactly as the endpoint sent them.</summary>
    private async Task<IReadOnlyList<float[]?>?> SendAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RequestUri)
            {
                Content = new StringContent(
                    BuildRequest(texts).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            Authorize(request);

            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller's cancellation. A slow provider is a
            // failed request, and the queue will offer these texts again.
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            JsonNode? body;
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                body = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }

            return ReadResponse(body, texts.Count);
        }
    }

    /// <summary>
    /// Adds bearer auth if a key is available.
    /// </summary>
    /// <remarks>
    /// Overridden rather than configured with the key itself, because the key is named by
    /// environment variable in <c>config.toml</c> and never stored in it — <c>doctor</c> prints
    /// that file, the installer backs it up, and a secret in it would end up in both.
    /// </remarks>
    protected virtual void Authorize(HttpRequestMessage request)
    {
    }

    protected static Uri Combine(Uri endpoint, string suffix)
    {
        var basePath = endpoint.AbsoluteUri.TrimEnd('/');
        return basePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? new Uri(basePath)
            : new Uri($"{basePath}{suffix}");
    }

    protected static float[]? ReadVector(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var vector = new float[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonValue value || !value.TryGetValue<double>(out var component))
            {
                return null;
            }

            vector[i] = (float)component;
        }

        return vector;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        gate.Dispose();
        if (ownsClient)
        {
            client.Dispose();
        }
    }
}
