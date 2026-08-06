using LLama;
using LLama.Common;
using LLama.Native;

namespace Engram.Core;

/// <summary>
/// Owns the llama.cpp weights that back <c>provider = "local"</c>, for as long as the process that
/// created it lives.
/// </summary>
/// <remarks>
/// <para><b>Why this is not inside <see cref="EmbedderFactory"/>.</b> Creating an embedder is cheap
/// and unowned everywhere it happens: <c>RetrievalExplainer</c> calls the factory purely to ask
/// whether a vector lane exists and drops the result, and nothing disposes what it gets. Were the
/// local case to load weights there, a readiness check would pull hundreds of megabytes into memory
/// and every recall would leak a copy. So loading lives here, behind an object somebody has to hold
/// and can therefore dispose, and the factory only ever attaches to a runtime already loaded.</para>
///
/// <para>One model at a time. Asking for a different one unloads the current weights and loads the
/// other, because two resident models is a memory decision nobody made.</para>
///
/// <para>Not thread-safe by accident: a lock guards the transition, since the whole point is that
/// several recalls arriving at once must wait for one load rather than start several.</para>
/// </remarks>
public sealed class LocalRuntime : IDisposable
{
    private readonly EngramHome home;
    private readonly Lock gate = new();
    private LLamaWeights? weights;
    private LLamaEmbedder? context;
    private LLamaSharpEmbedder? embedder;
    private string? loaded;
    private bool disposed;

    public LocalRuntime(EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        this.home = home;
    }

    /// <summary>The model currently loaded, or null if none is.</summary>
    public string? Loaded
    {
        get
        {
            lock (gate)
            {
                return loaded;
            }
        }
    }

    /// <summary>An embedder for <paramref name="modelId"/>, loading the weights if needed.</summary>
    /// <remarks>
    /// Blocks for as long as the model takes to load — seconds, cold. That cost is why callers that
    /// merely want to know whether local embedding is possible must not come through here.
    /// </remarks>
    public LocalRuntimeEmbedder Open(string modelId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (EmbeddingModels.Find(modelId) is not { } model)
        {
            return LocalRuntimeEmbedder.Failed(
                $"\"{modelId}\" is not a model Engram knows. Available: "
                + string.Join(", ", EmbeddingModels.All.Select(m => m.Id)) + ".");
        }

        var path = Path.Combine(home.ModelsDir, model.FileName);
        if (!File.Exists(path))
        {
            return LocalRuntimeEmbedder.Failed(
                $"{model.Id} is not downloaded yet. Run 'engram model install {model.Id}'.");
        }

        lock (gate)
        {
            if (embedder is not null && loaded == model.Id)
            {
                return new LocalRuntimeEmbedder(embedder, $"{model.Id} already loaded");
            }

            Unload();

            LlamaNative.Prepare();

            try
            {
                var parameters = ParametersFor(model, path);

                weights = LLamaWeights.LoadFromFile(parameters);
                context = new LLamaEmbedder(weights, parameters);

                if (context.EmbeddingSize != model.Dimensions)
                {
                    // The registry is what built the vec0 table, so a model file disagreeing with
                    // it is not something to route around: every vector written from here would be
                    // the wrong shape, and the insert error would name the table rather than this.
                    var actual = context.EmbeddingSize;
                    Unload();
                    return LocalRuntimeEmbedder.Failed(
                        $"{model.Id} produces {actual} dimensions, but Engram's registry says "
                        + $"{model.Dimensions}. The model file does not match the row that "
                        + "describes it.");
                }

                embedder = new LLamaSharpEmbedder(new EmbeddingSpace(model.Id, model.Dimensions), context);
                loaded = model.Id;
                return new LocalRuntimeEmbedder(embedder, $"loaded {model.Id} from {path}");
            }
#pragma warning disable CA1031 // A model that will not load is a reason, never an exception.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // The vector lane is optional, so failing to load weights has to degrade to
                // lexical recall with a sentence explaining why — not take the process down. The
                // likely causes are all environmental: a truncated download, a GGUF a newer
                // llama.cpp wrote, or a native library that will not load on this host.
                //
                // llama.cpp's own last words are appended because the managed exception rarely
                // names the cause and the log almost always does.
                Unload();
                var log = LlamaNative.RecentProblems();
                var detail = log.Count > 0 ? " — " + string.Join(" ", log) : string.Empty;
                return LocalRuntimeEmbedder.Failed(
                    $"Could not load {model.Id} from {path}: {ex.Message}{detail}");
            }
        }
    }

    /// <summary>The context window actually used, which is not always the model's.</summary>
    /// <remarks>
    /// Capped because a pooled embedding has to fit in one physical batch, so the batch buffers are
    /// sized to this number — and at qwen3's 32k that allocation is far larger than anything Engram
    /// would put through it. Facts and queries are sentences. 8k is already generous for both, and
    /// the two models under the cap are unaffected.
    /// </remarks>
    public const int MaxContext = 8192;

    /// <summary>
    /// How <paramref name="model"/> is loaded. Separate from loading it so the settings can be
    /// asserted without paying seconds to read a GGUF — and because three of them are silent when
    /// wrong, which is exactly the kind that needs a test rather than a reading.
    /// </summary>
    public static ModelParams ParametersFor(EmbeddingModel model, string path)
    {
        ArgumentNullException.ThrowIfNull(model);

        var context = (uint)Math.Min(model.ContextTokens, MaxContext);

        return new ModelParams(path)
        {
            Embeddings = true,
            PoolingType = model.Pooling,
            ContextSize = context,

            // An encoder scores every token against every other in one pass, so llama.cpp requires
            // the micro-batch to hold the whole sequence — unlike a generative model, which
            // streams. Left at the 512 default, any text longer than that fails to embed at all,
            // which would silently cap what the vector lane can see to a fraction of the context
            // the model advertises.
            BatchSize = context,
            UBatchSize = context,

            // Offload everything the accelerator will take. llama.cpp clamps this to the layer
            // count, and a build with no GPU backend ignores it, so one number is correct on
            // Metal, on CUDA and on a machine with neither.
            GpuLayerCount = 99,
        };
    }

    /// <summary>Releases the weights. Callers must already hold <see cref="gate"/>.</summary>
    private void Unload()
    {
        embedder = null;
        loaded = null;
        context?.Dispose();
        context = null;
        weights?.Dispose();
        weights = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        lock (gate)
        {
            Unload();
        }
    }
}

/// <summary>An embedder backed by locally loaded weights, or the reason there is not one.</summary>
public sealed record LocalRuntimeEmbedder(IEmbedder? Embedder, string Reason)
{
    public bool Open => Embedder is not null;

    public static LocalRuntimeEmbedder Failed(string reason) => new(null, reason);
}
