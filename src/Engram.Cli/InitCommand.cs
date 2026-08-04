using Engram.Core;

namespace Engram.Cli;

internal static class InitCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 0)
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var results = EngramInitializer.Initialize(home);

        foreach (var result in results)
        {
            stdout.WriteLine(result.Created ? result.Path : $"{result.Path} already exists");
        }

        return 0;
    }
}
