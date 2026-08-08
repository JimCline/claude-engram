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
/// <param name="Ignored">
/// Settings Engram itself once wrote here and no longer reads, described. Kept apart from
/// <paramref name="Problems"/> deliberately: a retired key is not a misconfiguration, and folding
/// it in would clear <see cref="IsUsable"/> and switch off the vector lane of everyone whose config
/// predates the change.
/// </param>
public sealed record EmbeddingSettings(
    EmbeddingProvider Provider,
    string? Model,
    int? Dimensions,
    int MaxBatch,
    string? Endpoint,
    string? ApiKeyEnvironmentVariable,
    TimeSpan Timeout,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Ignored)
{
    public const string Section = "embedding";
    public const int DefaultMaxBatch = 16;
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>
    /// Keys this section used to have, and what answers for them now.
    /// </summary>
    /// <remarks>
    /// <para>An explicit list rather than "anything not in the shipped default". The parser is
    /// lenient about unknown keys on purpose — that is how a config survives a version bump and how
    /// someone leaves themselves a note — so warning about every key Engram does not recognise would
    /// report a user's own choice as a fault, which D37 says is how people learn to stop reading
    /// <c>doctor</c>. These three are different: Engram wrote them, they read exactly like live
    /// settings, and <c>model_path</c> in particular looks like it selects the weights when the
    /// model file has been chosen from <see cref="EmbeddingModels"/> ever since the embedder moved
    /// inside the server. <c>ConfigEditor</c> only ever rewrites the single line it owns, so
    /// anything retired stays in the file forever unless something says so.</para>
    /// </remarks>
    public static IReadOnlyList<(string Key, string Note)> Retired { get; } =
    [
        ("model_path", "the weights are chosen by `model` from Engram's own list — `engram model list`"),
        ("threads", "llama.cpp's thread count is no longer configurable here"),
        ("idle_unload_minutes", "the model is held by the server for as long as it runs"),
    ];

    public static EmbeddingSettings Disabled { get; } = new(
        EmbeddingProvider.None, null, null, DefaultMaxBatch, null, null,
        TimeSpan.FromSeconds(DefaultTimeoutSeconds), [], []);

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

        var ignored = new List<string>();
        foreach (var (key, note) in Retired)
        {
            if (config.Raw(Section, key) is not null)
            {
                ignored.Add($"{key} — {note}");
            }
        }

        return new EmbeddingSettings(
            provider,
            model,
            dimensions,
            maxBatch,
            endpoint,
            config.String(Section, "api_key_env"),
            TimeSpan.FromSeconds(timeout),
            problems,
            ignored);
    }

    private static EmbeddingProvider Unknown(List<string> problems, string name)
    {
        problems.Add(
            $"[embedding] provider \"{name}\" is not one of: none, local, openai-compat, ollama.");
        return EmbeddingProvider.None;
    }
}
