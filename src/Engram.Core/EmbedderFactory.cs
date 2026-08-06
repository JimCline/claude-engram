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
    public static EmbedderResolution Create(
        EmbeddingSettings settings,
        Func<string, string?> environment)
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
                    ? new OllamaEmbedder(space, endpoint, settings.Timeout)
                    : new OpenAiCompatibleEmbedder(space, endpoint, settings.Timeout, key);

                return new EmbedderResolution(embedder, $"{settings.Provider} at {endpoint}");

            case EmbeddingProvider.Local:
                return EmbedderResolution.Unavailable(
                    "[embedding] provider = \"local\" is not wired up yet. Point at a local "
                    + "runtime with provider = \"ollama\" or \"openai-compat\" in the meantime.");

            default:
                return EmbedderResolution.Unavailable($"Unhandled provider {settings.Provider}.");
        }
    }

    private static bool IsLoopback(Uri endpoint) =>
        endpoint.IsLoopback
        || string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}
