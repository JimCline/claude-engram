namespace Engram.Core;

/// <summary>An embedder, or the reason there is not one.</summary>
public sealed record EmbedderResolution(IEmbedder? Embedder, string Reason)
{
    public bool Resolved => Embedder is not null;

    public static EmbedderResolution Unavailable(string reason) => new(null, reason);
}

/// <summary>
/// Turns <see cref="EmbeddingSettings"/> into a provider.
/// </summary>
/// <remarks>
/// Never throws, and never returns a null-object embedder. Absence is how embeddings-off is
/// represented throughout (D18, <see cref="IEmbedder"/>), so every failure to construct one
/// arrives as an unresolved result carrying a sentence a user can act on — which is what
/// <c>doctor</c> prints and what keeps "off" distinguishable from "on and broken".
/// </remarks>
public static class EmbedderFactory
{
    /// <summary>
    /// Builds the configured provider.
    /// </summary>
    /// <param name="settings">The parsed <c>[embedding]</c> section.</param>
    /// <param name="environment">
    /// Reads an environment variable by name. Injected because the API key is named in config
    /// rather than stored there, and because a test must be able to supply one without setting
    /// a real variable for the whole process.
    /// </param>
    /// <param name="client">An HTTP client to borrow rather than own.</param>
    /// <param name="local">
    /// The host for <c>provider = "local"</c>, supplied only by a caller long-lived enough to own
    /// loaded model weights. Without one the local provider resolves to a reason rather than an
    /// embedder — see <see cref="LocalRuntime"/> for why loading cannot happen here.
    /// </param>
    public static EmbedderResolution Create(
        EmbeddingSettings settings,
        Func<string, string?> environment,
        HttpClient? client = null,
        LocalRuntime? local = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);

        if (settings.Provider == EmbeddingProvider.None)
        {
            return EmbedderResolution.Unavailable(
                "Embeddings are off. Recall uses the lexical lane only, which is a supported "
                + "configuration — set [embedding] provider to turn the vector lane on.");
        }

        if (settings.Problems.Count > 0)
        {
            return EmbedderResolution.Unavailable(string.Join(" ", settings.Problems));
        }

        if (settings.Space is not { } space)
        {
            return EmbedderResolution.Unavailable(
                "[embedding] is missing the model or dim needed to name an embedding space.");
        }

        switch (settings.Provider)
        {
            case EmbeddingProvider.OpenAiCompatible:
            case EmbeddingProvider.Ollama:
                var endpoint = new Uri(settings.Endpoint!, UriKind.Absolute);
                var key = settings.ApiKeyEnvironmentVariable is { Length: > 0 } name
                    ? environment(name)
                    : null;

                if (settings.ApiKeyEnvironmentVariable is { Length: > 0 } named
                    && string.IsNullOrEmpty(key)
                    && !IsLoopback(endpoint))
                {
                    // Only worth complaining about for a remote endpoint. A local runtime
                    // ignores the header, so demanding a key there would turn a working setup
                    // into a broken one over a setting that does nothing.
                    return EmbedderResolution.Unavailable(
                        $"[embedding] api_key_env names {named}, but that environment variable "
                        + $"is empty and {endpoint.Host} is not local.");
                }

                IEmbedder embedder = settings.Provider == EmbeddingProvider.Ollama
                    ? new OllamaEmbedder(space, endpoint, settings.Timeout, client)
                    : new OpenAiCompatibleEmbedder(space, endpoint, settings.Timeout, key, client);

                return new EmbedderResolution(embedder, $"{settings.Provider} at {endpoint}");

            case EmbeddingProvider.Local:
                if (local is null)
                {
                    return EmbedderResolution.Unavailable(
                        "[embedding] provider = \"local\" runs the model inside the Engram server, "
                        + "and this process is not it. Start the server with `engram serve`, or "
                        + "point at a runtime you started yourself with provider = \"openai-compat\".");
                }

                var opened = local.Open(space.Model);
                if (opened.Embedder is not { } loaded)
                {
                    return EmbedderResolution.Unavailable(opened.Reason);
                }

                // The runtime's own instance, handed out rather than wrapped again: the weights
                // behind it are the expensive thing and exactly one process-wide copy exists. It
                // owns nothing, so this stays as free to drop as the remote providers above.
                return new EmbedderResolution(loaded, $"local {space.Model} — {opened.Reason}");

            default:
                return EmbedderResolution.Unavailable($"Unhandled provider {settings.Provider}.");
        }
    }

    private static bool IsLoopback(Uri endpoint) =>
        endpoint.IsLoopback
        || string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}
