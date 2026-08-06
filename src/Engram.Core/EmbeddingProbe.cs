using System.Diagnostics;

namespace Engram.Core;

/// <summary>What the endpoint said when asked how wide its vectors are.</summary>
public sealed record ProbeResult(int? Dimensions, string Reason, TimeSpan Elapsed)
{
    public bool Answered => Dimensions is > 0;

    public static ProbeResult Silent(string reason) => new(null, reason, TimeSpan.Zero);
}

/// <summary>
/// Asks an embedding endpoint how wide its vectors are, rather than making someone look it up.
/// </summary>
/// <remarks>
/// <para><b>Why this is worth a round trip.</b> <c>dim</c> is the one embedding setting that does
/// not fail loudly when it is wrong. A wrong endpoint URL refuses to connect; a wrong model name
/// comes back as an error; a wrong width produces vectors that are stored, compared, and ranked,
/// and that never match anything — retrieval quietly degrades and nothing anywhere reports a
/// fault. The number is also not knowable from the model name, since an endpoint may serve a
/// quantized or truncated variant under the same label. Asking replaces a lookup with an
/// observation, which is the only form of this answer that can be trusted.</para>
///
/// <para><b>It runs against a configuration that is incomplete by definition.</b> That is the
/// point, so the settings' own problem list is set aside for the probe — it will be complaining
/// that <c>dim</c> is missing, which is what the probe was called to find out. The caller still
/// sees those problems; they are just not grounds for refusing to ask. Everything else the factory
/// checks still applies: the provider, the endpoint, and the API key rule for a non-local host.</para>
/// </remarks>
public static class EmbeddingProbe
{
    /// <summary>
    /// Stands in for the width while it is unknown.
    /// </summary>
    /// <remarks>
    /// <see cref="EmbeddingSpace"/> refuses a non-positive width, correctly — a zero-width space is
    /// meaningless everywhere else. One is the smallest lie that constructs, and
    /// <see cref="HttpEmbedder.ProbeWidthAsync"/> is the only code that ever sees it, where it is
    /// never compared against anything.
    /// </remarks>
    public const int ProvisionalWidth = 1;

    public static ProbeResult Run(
        EmbeddingSettings settings,
        Func<string, string?> environment,
        HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Provider is EmbeddingProvider.None)
        {
            return ProbeResult.Silent("Embeddings are off — there is no endpoint to ask.");
        }

        if (settings.Provider is EmbeddingProvider.Local)
        {
            // Nothing to ask: the width is a property of the file, and Engram already knows it for
            // every model it will run itself.
            return EmbeddingModels.Find(settings.Model) is { } known
                ? new ProbeResult(known.Dimensions, $"{known.Id} is {known.Dimensions} wide", TimeSpan.Zero)
                : ProbeResult.Silent($"[embedding] model \"{settings.Model}\" is not one Engram knows.");
        }

        if (settings.Endpoint is not { Length: > 0 })
        {
            return ProbeResult.Silent("[embedding] endpoint is not set, so there is nothing to ask.");
        }

        if (settings.Model is not { Length: > 0 })
        {
            // Both request shapes name the model, so there is no meaningful request to send.
            return ProbeResult.Silent("[embedding] model is not set, and the request has to name one.");
        }

        var resolution = EmbedderFactory.Create(ForProbe(settings), environment, client);
        if (resolution.Embedder is not HttpEmbedder embedder)
        {
            return ProbeResult.Silent(resolution.Reason);
        }

        using (embedder)
        {
            var clock = Stopwatch.StartNew();
            var width = embedder.ProbeWidthAsync().GetAwaiter().GetResult();
            clock.Stop();

            return width is > 0
                ? new ProbeResult(width, $"{settings.Endpoint} answered", clock.Elapsed)
                : new ProbeResult(
                    null,
                    $"{settings.Endpoint} did not answer with a vector — check that it is running, "
                        + $"that it serves this provider's API, and that it knows a model called \"{settings.Model}\".",
                    clock.Elapsed);
        }
    }

    /// <summary>The same settings, made constructible while the width is still the question.</summary>
    public static EmbeddingSettings ForProbe(EmbeddingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings with
        {
            Dimensions = settings.Dimensions ?? ProvisionalWidth,
            Problems = [],
        };
    }
}
