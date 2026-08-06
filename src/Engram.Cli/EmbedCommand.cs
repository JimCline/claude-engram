using System.Globalization;
using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// <c>engram embed --probe</c> — ask the configured endpoint how wide its vectors are.
/// </summary>
/// <remarks>
/// Reading only, unless <c>--use-it</c> is given: the probe is the thing you run when you are not
/// sure what is there, and a diagnostic that edits your config as a side effect is not one.
/// </remarks>
public static class EmbedCommand
{
    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (!args.Contains("--probe"))
        {
            stderr.WriteLine("error: engram embed needs --probe. Nothing else is wired up yet.");
            return 2;
        }

        var home = EngramHome.ResolveFromProcess(homePath);
        var config = ConfigFile.Load(home.ConfigPath);
        var configured = EmbeddingSettings.Read(config);

        var settings = Override(configured, args, out var overridden);
        if (settings is null)
        {
            stderr.WriteLine("error: --provider must be one of none, local, openai-compat, ollama.");
            return 1;
        }

        foreach (var problem in configured.Problems.Where(_ => !overridden))
        {
            stdout.WriteLine("note: " + problem);
        }

        var result = EmbeddingProbe.Run(settings, Environment.GetEnvironmentVariable);

        if (!result.Answered)
        {
            stderr.WriteLine("error: " + result.Reason);
            return 1;
        }

        stdout.WriteLine($"{settings.Provider} — {result.Dimensions} dimensions ({result.Reason}"
            + (result.Elapsed > TimeSpan.Zero
                ? $", {result.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms)"
                : ")"));

        if (configured.Dimensions is { } stated && stated != result.Dimensions)
        {
            stdout.WriteLine();
            stdout.WriteLine($"Your config says dim = {stated}, which does not match. A wrong width does not "
                + "error anywhere — it stores vectors that never match a query.");
        }

        if (!args.Contains("--use-it"))
        {
            stdout.WriteLine();
            stdout.WriteLine("Add --use-it to write this into the config.");
            return 0;
        }

        stdout.WriteLine();
        return EmbeddingSetup.Apply(
            home,
            new EmbeddingChoice(
                Name(settings.Provider),
                settings.Model,
                settings.Endpoint,
                result.Dimensions,
                settings.ApiKeyEnvironmentVariable),
            args.Contains("--force"),
            DateTimeOffset.UtcNow,
            stdout,
            stderr);
    }

    /// <summary>The probe the picker uses, so its endpoint rung can stop asking for a width.</summary>
    public static int? ProbeWidth(string provider, string endpoint, string model)
    {
        var settings = EmbeddingSettings.Disabled with
        {
            Provider = provider == "ollama" ? EmbeddingProvider.Ollama : EmbeddingProvider.OpenAiCompatible,
            Endpoint = endpoint,
            Model = model,
        };

        return EmbeddingProbe.Run(settings, Environment.GetEnvironmentVariable).Dimensions;
    }

    private static EmbeddingSettings? Override(EmbeddingSettings settings, string[] args, out bool overridden)
    {
        var provider = Value(args, "--provider");
        var endpoint = Value(args, "--endpoint");
        var model = Value(args, "--model");
        overridden = provider is not null || endpoint is not null || model is not null;

        if (provider is not null)
        {
            if (Parse(provider) is not { } parsed)
            {
                return null;
            }

            settings = settings with { Provider = parsed };
        }

        if (endpoint is not null)
        {
            settings = settings with { Endpoint = endpoint };
        }

        if (model is not null)
        {
            settings = settings with { Model = model };
        }

        // A flag naming an endpoint outranks whatever the config's problem list has to say about
        // the config, which is not what is being probed.
        return overridden ? settings with { Problems = [] } : settings;
    }

    private static EmbeddingProvider? Parse(string name) => name.ToLowerInvariant() switch
    {
        "none" or "off" or "disabled" => EmbeddingProvider.None,
        "local" => EmbeddingProvider.Local,
        "openai-compat" or "openai" => EmbeddingProvider.OpenAiCompatible,
        "ollama" => EmbeddingProvider.Ollama,
        _ => null,
    };

    private static string Name(EmbeddingProvider provider) => provider switch
    {
        EmbeddingProvider.Local => "local",
        EmbeddingProvider.Ollama => "ollama",
        EmbeddingProvider.OpenAiCompatible => "openai-compat",
        _ => "none",
    };

    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith('-')
            ? args[index + 1]
            : null;
    }
}
