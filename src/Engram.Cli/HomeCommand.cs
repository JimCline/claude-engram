using Engram.Core;

namespace Engram.Cli;

internal static class HomeCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 0)
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        stdout.WriteLine($"Root={home.Root}");
        stdout.WriteLine($"DatabasePath={home.DatabasePath}");
        stdout.WriteLine($"ConfigPath={home.ConfigPath}");
        stdout.WriteLine($"LogPath={home.LogPath}");
        stdout.WriteLine($"ModelsDir={home.ModelsDir}");
        stdout.WriteLine($"QueueDir={home.QueueDir}");
        stdout.WriteLine($"ReportDir={home.ReportDir}");
        return 0;
    }
}
