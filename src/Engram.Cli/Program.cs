using Engram.Core;

namespace Engram.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "home")
        {
            var home = EngramHome.ResolveFromProcess(explicitPath: null);
            Console.WriteLine($"Root={home.Root}");
            Console.WriteLine($"DatabasePath={home.DatabasePath}");
            Console.WriteLine($"ConfigPath={home.ConfigPath}");
            Console.WriteLine($"LogPath={home.LogPath}");
            Console.WriteLine($"ModelsDir={home.ModelsDir}");
            Console.WriteLine($"QueueDir={home.QueueDir}");
            Console.WriteLine($"ReportDir={home.ReportDir}");
            return 0;
        }

        Console.WriteLine("usage: engram <home>");
        return 1;
    }
}
