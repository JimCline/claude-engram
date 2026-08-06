using System.Globalization;
using Engram.Core;

namespace Engram.Cli;

internal static class InitCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (!Understood(rest))
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        var withEmbeddings = rest.Contains("--with-embeddings");
        var force = rest.Contains("--force");
        var provider = Value(rest, "--provider");
        var model = Value(rest, "--model");
        var endpoint = Value(rest, "--endpoint");
        var dimensions = Value(rest, "--dim");
        var apiKeyVariable = Value(rest, "--api-key-env");

        var home = EngramHome.ResolveFromProcess(homePath);
        var results = EngramInitializer.Initialize(home);

        foreach (var result in results)
        {
            stdout.WriteLine(result.Created ? result.Path : $"{result.Path} already exists");
        }

        if (!withEmbeddings && provider is null)
        {
            return 0;
        }

        stdout.WriteLine();

        if (provider is not null)
        {
            int? width = null;
            if (dimensions is not null)
            {
                if (!int.TryParse(dimensions, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                {
                    stderr.WriteLine($"error: --dim must be a positive number, not '{dimensions}'.");
                    return 1;
                }

                width = parsed;
            }

            return Land(home, new EmbeddingChoice(provider, model, endpoint, width, apiKeyVariable), force, stdout, stderr);
        }

        EmbeddingSetup.Describe(stdout, home);
        stdout.WriteLine();

        // Piped or redirected, there is nobody to answer, and a prompt that reads EOF as an answer
        // would pick something on the user's behalf. The flags below say the same thing without
        // needing a terminal.
        if (Console.IsInputRedirected)
        {
            stdout.WriteLine("Not a terminal, so nothing was changed. Say it directly instead:");
            stdout.WriteLine("  engram init --provider none");
            stdout.WriteLine($"  engram init --provider local --model {EmbeddingModels.DefaultId}");
            stdout.WriteLine("  engram init --provider openai-compat --endpoint http://localhost:1234/v1 --dim 768");
            return 0;
        }

        if (EmbeddingSetup.Ask(Console.In, stdout) is not { } choice)
        {
            stdout.WriteLine();
            stdout.WriteLine("Left the config alone.");
            return 0;
        }

        stdout.WriteLine();
        return Land(home, choice, force, stdout, stderr);
    }

    private static int Land(
        EngramHome home,
        EmbeddingChoice choice,
        bool force,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (choice.Provider == "local")
        {
            var id = choice.Model ?? EmbeddingModels.DefaultId;
            if (EmbeddingModels.Find(id) is not { } model)
            {
                stderr.WriteLine($"error: no local model called '{id}'. Run 'engram model list' to see them.");
                return 1;
            }

            // The download comes first. A config naming a model that is not on disk describes an
            // instance that cannot start, and it would have been written by the very command the
            // user ran to avoid getting this wrong by hand.
            if (ModelCommand.Fetch(home, model, stdout, stderr) != 0)
            {
                return 1;
            }

            choice = choice with { Model = model.Id };
            stdout.WriteLine();
        }

        return EmbeddingSetup.Apply(home, choice, force, DateTimeOffset.UtcNow, stdout, stderr);
    }

    private static readonly string[] Switches = ["--with-embeddings", "--force"];

    private static readonly string[] Settings = ["--provider", "--model", "--endpoint", "--dim", "--api-key-env"];

    private static bool Understood(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (Switches.Contains(args[i]))
            {
                continue;
            }

            if (Settings.Contains(args[i]) && i + 1 < args.Length)
            {
                i++;
                continue;
            }

            return false;
        }

        return true;
    }

    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith('-')
            ? args[index + 1]
            : null;
    }
}
