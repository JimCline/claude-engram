namespace Engram.Core;

public enum EmbeddingProvider
{
    /// <summary>No vector lane. FTS5-only is fully supported, not degraded (D18).</summary>
    None,

    /// <summary>
    /// A GGUF model from <see cref="EmbeddingModels"/>, loaded into this process by llama.cpp.
    /// </summary>
    Local,

    /// <summary>
    /// Anything speaking <c>POST /v1/embeddings</c> — LM Studio, llama.cpp's server, vLLM,
    /// OpenAI, Voyage, and most hosted providers.
    /// </summary>
    OpenAiCompatible,

    /// <summary>Ollama's native <c>POST /api/embed</c>, whose response shape differs.</summary>
    Ollama,
}

/// <summary>
/// The <c>[embedding]</c> section, read once and answerable without touching a model.
/// </summary>
/// <remarks>
/// <para><b>Misconfiguration is reported, never thrown.</b> The vector lane is optional, so a
/// bad endpoint or a missing width must leave Engram working with FTS5 and able to say why —
/// not refuse to start. Every reason lands in <see cref="Problems"/>, which is what
/// <c>doctor</c> prints and what keeps "embeddings are off" distinguishable from "embeddings
/// are on and broken".</para>
/// </remarks>
public sealed record EmbeddingSettings(
    EmbeddingProvider Provider,
    string? Model,
    int? Dimensions,
    int MaxBatch,
    string? Endpoint,
    string? ApiKeyEnvironmentVariable,
    TimeSpan Timeout,
    IReadOnlyList<string> Problems)
{
    public const string Section = "embedding";
    public const int DefaultMaxBatch = 16;
    public const int DefaultTimeoutSeconds = 60;

    public static EmbeddingSettings Disabled { get; } = new(
        EmbeddingProvider.None, null, null, DefaultMaxBatch, null, null,
        TimeSpan.FromSeconds(DefaultTimeoutSeconds), []);

    /// <summary>True when a provider is configured and nothing is wrong with how.</summary>
    public bool IsUsable => Provider != EmbeddingProvider.None && Problems.Count == 0;

    /// <summary>
    /// The space this configuration produces into, or null if it cannot be known without
    /// asking the provider.
    /// </summary>
    public EmbeddingSpace? Space =>
        IsUsable && Model is { Length: > 0 } && Dimensions is { } width
            ? new EmbeddingSpace(Model, width)
            : null;

    public static EmbeddingSettings Read(ConfigFile config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();
        var name = config.String(Section, "provider") ?? "none";
        var provider = name.ToLowerInvariant() switch
        {
            "none" or "off" or "disabled" => EmbeddingProvider.None,
            "local" => EmbeddingProvider.Local,
            "openai-compat" or "openai" => EmbeddingProvider.OpenAiCompatible,
            "ollama" => EmbeddingProvider.Ollama,
            _ => Unknown(problems, name),
        };

        var model = config.String(Section, "model");
        var dimensions = config.Int(Section, "dim");
        var endpoint = config.String(Section, "endpoint");
        var maxBatch = config.Int(Section, "max_batch") ?? DefaultMaxBatch;
        var timeout = config.Int(Section, "timeout_seconds") ?? DefaultTimeoutSeconds;

        if (maxBatch <= 0)
        {
            problems.Add($"[embedding] max_batch must be positive; found {maxBatch}.");
            maxBatch = DefaultMaxBatch;
        }

        if (timeout <= 0)
        {
            problems.Add($"[embedding] timeout_seconds must be positive; found {timeout}.");
            timeout = DefaultTimeoutSeconds;
        }

        switch (provider)
        {
            case EmbeddingProvider.Local:
                var known = EmbeddingModels.Find(model);
                if (known is null)
                {
                    problems.Add(
                        $"[embedding] model \"{model}\" is not one Engram knows. Available: "
                        + string.Join(", ", EmbeddingModels.All.Select(m => m.Id)) + ".");
                }
                else
                {
                    // The registry is the authority on width for its own models. A config that
                    // disagrees is a user who changed the model and forgot the dimension, which
                    // would build an index of the wrong shape and fail at the first insert.
                    if (dimensions is { } stated && stated != known.Dimensions)
                    {
                        problems.Add(
                            $"[embedding] dim = {stated} contradicts {known.Id}, which produces "
                            + $"{known.Dimensions}. Remove dim, or change the model.");
                    }

                    dimensions = known.Dimensions;
                }

                break;

            case EmbeddingProvider.OpenAiCompatible:
            case EmbeddingProvider.Ollama:
                if (endpoint is null)
                {
                    problems.Add($"[embedding] provider = \"{name}\" needs an endpoint.");
                }
                else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
                         || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                {
                    problems.Add($"[embedding] endpoint \"{endpoint}\" is not an http or https URL.");
                }

                if (model is null)
                {
                    problems.Add($"[embedding] provider = \"{name}\" needs a model name.");
                }

                // Required rather than discovered, because VectorIndex has to create a table of
                // a fixed width before the first vector exists, and IEmbedder promises a space
                // that is answerable without a round trip. `engram embed --probe` fills this in
                // by asking the endpoint once, so nobody has to look it up.
                if (dimensions is null)
                {
                    problems.Add(
                        $"[embedding] provider = \"{name}\" needs dim — the vector width the "
                        + "endpoint returns. Run `engram embed --probe` to detect it.");
                }
                else if (dimensions <= 0)
                {
                    problems.Add($"[embedding] dim must be positive; found {dimensions}.");
                }

                break;
        }

        return new EmbeddingSettings(
            provider,
            model,
            dimensions,
            maxBatch,
            endpoint,
            config.String(Section, "api_key_env"),
            TimeSpan.FromSeconds(timeout),
            problems);
    }

    private static EmbeddingProvider Unknown(List<string> problems, string name)
    {
        problems.Add(
            $"[embedding] provider \"{name}\" is not one of: none, local, openai-compat, ollama.");
        return EmbeddingProvider.None;
    }
}
