using Engram.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Engram.Cli;

/// <summary>
/// Hosts <see cref="IndexFreshness"/> for the life of the server (spec §6.1).
/// </summary>
/// <remarks>
/// A separate daemon was rejected: it would need its own pid file, its own liveness story, and its
/// own instance of the process-identity problem D42 exists to solve, for a loop that needs the same
/// home, config and database the server already has. Config is read once at startup, same as
/// <see cref="EmbeddingBacklogService"/> — a server that starts with the setting off stays off until
/// restarted, rather than picking up a change mid-run.
/// </remarks>
internal sealed class IndexFreshnessService(
    EngramHome home, ILogger<IndexFreshnessService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = IndexingSettings.Read(ConfigFile.Load(home.ConfigPath));

        if (!settings.AutoIndexInBackground)
        {
            // Asking for a provider and not getting one is a warning; a setting nobody turned on
            // is not news (EmbeddingBacklogService's same distinction), so this is Information.
            logger.LogInformation(
                "Background indexing is off: auto_index_in_background is false.");

            // Recorded as well as logged: "why is indexing.json not moving" is exactly the
            // question a declining service must answer, per D54's measured lesson.
            IndexProgress.WriteUnavailable(home, "auto_index_in_background is false");
            return;
        }

        await new IndexFreshness(
                home,
                message => logger.LogInformation("{Message}", message))
            .RunAsync(stoppingToken)
            .ConfigureAwait(false);
    }
}
