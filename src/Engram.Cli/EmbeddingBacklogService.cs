using Engram.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Engram.Cli;

/// <summary>
/// Hosts <see cref="EmbeddingBacklog"/> for the life of the server.
/// </summary>
/// <remarks>
/// This is the only place an embedder is constructed. Engram runs one server per home — the pid
/// file already guarantees it — so putting the loop here makes the singular embedding service
/// fall out of the process model rather than needing a lock, a lease, or a second daemon to
/// enforce. Hooks and one-shot CLI invocations write facts and exit; their vectors land here.
///
/// <para>Configuration is read once, at startup, and a server that starts with embeddings off
/// stays off until it is restarted. That is deliberate: re-reading config on a timer would make
/// the embedding space change under a half-built index, which is the one thing D18's space pin
/// exists to prevent.</para>
/// </remarks>
internal sealed class EmbeddingBacklogService(
    EngramHome home,
    LocalRuntime local,
    ILogger<EmbeddingBacklogService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = EmbeddingSettings.Read(ConfigFile.Load(home.ConfigPath));
        var resolution = EmbedderFactory.Create(
            settings, Environment.GetEnvironmentVariable, client: null, local);

        if (resolution.Embedder is not { } embedder)
        {
            // Asking for a provider and not getting one is a warning; asking for none and
            // getting none is not news, and warning about it on every start would train
            // everyone to ignore this log.
            if (settings.Provider == EmbeddingProvider.None)
            {
                logger.LogInformation("Embeddings are off: {Reason}", resolution.Reason);
            }
            else
            {
                logger.LogWarning("Embeddings are unavailable: {Reason}", resolution.Reason);
            }

            return;
        }

        using var disposable = embedder as IDisposable;

        logger.LogInformation("Embedding provider: {Reason}", resolution.Reason);

        await new EmbeddingBacklog(
                home,
                embedder,
                settings,
                message => logger.LogInformation("{Message}", message))
            .RunAsync(stoppingToken)
            .ConfigureAwait(false);
    }
}
