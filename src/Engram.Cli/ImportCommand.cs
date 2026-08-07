using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram import</c> — adds a bundle's facts to the store. Dry run unless
/// <c>--apply</c>, additive and idempotent like the replay it shares its flow with: it
/// never rewrites or closes a fact the store already had.
/// </summary>
internal static class ImportCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var apply = rest.Contains("--apply");
        var file = rest.FirstOrDefault(a => !a.StartsWith('-'));

        if (file is null)
        {
            stderr.WriteLine("error: import needs a bundle file — 'engram import <file> [--apply]'");
            return 1;
        }

        if (rest.Any(a => a.StartsWith('-') && a != "--apply"))
        {
            stderr.WriteLine($"error: unexpected argument '{rest.First(a => a.StartsWith('-') && a != "--apply")}'");
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        return BackupCommand.ReplayInto(
            home,
            file,
            apply,
            $"error: no bundle at {file}",
            stdout,
            stderr);
    }
}
