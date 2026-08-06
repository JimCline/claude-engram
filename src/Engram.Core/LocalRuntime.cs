namespace Engram.Core;

/// <summary>
/// Owns the <c>llama-server</c> child that backs <c>provider = "local"</c>, for as long as the
/// process that created it lives.
/// </summary>
/// <remarks>
/// <para><b>Why this is not inside <see cref="EmbedderFactory"/>.</b> Creating an embedder is
/// cheap and unowned everywhere it happens: <c>RetrievalExplainer</c> calls the factory purely to
/// ask whether a vector lane exists and drops the result, and nothing disposes what it gets. Had
/// the local case launched a server there, a readiness check would have started a model and every
/// recall would have leaked one. So launching lives here, behind an object somebody has to hold
/// and can therefore dispose, and the factory only ever attaches to a runtime already going.</para>
///
/// <para>One model at a time. Asking for a different one stops the current server and starts
/// another, because two loaded models is a memory decision nobody made.</para>
///
/// <para>Not thread-safe by accident: a lock guards the transition, since the whole point is that
/// several recalls arriving at once must wait for one server rather than start several.</para>
/// </remarks>
public sealed class LocalRuntime : IDisposable
{
    private readonly EngramHome home;
    private readonly Func<string, string?> environment;
    private readonly Lock gate = new();
    private LlamaServerHandle? server;
    private string? loaded;
    private bool disposed;

    public LocalRuntime(EngramHome home, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(environment);
        this.home = home;
        this.environment = environment;
    }

    /// <summary>The model currently loaded, or null if no server is running.</summary>
    public string? Loaded
    {
        get
        {
            lock (gate)
            {
                return server is { Running: true } ? loaded : null;
            }
        }
    }

    /// <summary>The endpoint serving <paramref name="modelId"/>, starting a server if needed.</summary>
    /// <remarks>
    /// Blocks for as long as the model takes to load — seconds, cold. That cost is why callers
    /// that merely want to know whether local embedding is possible must not come through here.
    /// </remarks>
    public LocalRuntimeEndpoint Open(string modelId, string? configuredServerPath, TimeSpan startupTimeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (EmbeddingModels.Find(modelId) is not { } model)
        {
            return LocalRuntimeEndpoint.Failed(
                $"\"{modelId}\" is not a model Engram knows. Available: "
                + string.Join(", ", EmbeddingModels.All.Select(m => m.Id)) + ".");
        }

        lock (gate)
        {
            if (server is { Running: true } && loaded == model.Id)
            {
                return new LocalRuntimeEndpoint(server.Endpoint, $"already running on port {server.Port}");
            }

            // Either it died or it is holding the wrong model. Both mean this one is finished.
            server?.Dispose();
            server = null;
            loaded = null;

            if (LlamaServer.Locate(home, configuredServerPath, environment) is not { } location)
            {
                return LocalRuntimeEndpoint.Failed(
                    configuredServerPath is { Length: > 0 }
                        ? $"[embedding] server_path points at {configuredServerPath}, which is not there."
                        : "No llama-server found. " + LlamaServer.WhereItLooked(home));
            }

            var start = LlamaServer.Start(home, model, location, startupTimeout);
            if (start.Handle is not { } handle)
            {
                return LocalRuntimeEndpoint.Failed(start.Reason);
            }

            server = handle;
            loaded = model.Id;
            return new LocalRuntimeEndpoint(handle.Endpoint, start.Reason);
        }
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
            server?.Dispose();
            server = null;
            loaded = null;
        }
    }
}

/// <summary>A loopback endpoint serving a local model, or the reason there is not one.</summary>
public sealed record LocalRuntimeEndpoint(Uri? Endpoint, string Reason)
{
    public bool Open => Endpoint is not null;

    public static LocalRuntimeEndpoint Failed(string reason) => new(null, reason);
}
