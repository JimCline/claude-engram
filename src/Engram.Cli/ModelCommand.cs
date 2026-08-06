using Engram.Core;

namespace Engram.Cli;

/// <summary>
/// Lists and installs the local embedding models.
/// </summary>
/// <remarks>
/// Nothing here knows a model id. The subcommands walk <see cref="EmbeddingModels.All"/>, so a
/// new rung is one registry row and zero edits to this file — the same rule the registry's own
/// remarks state, enforced by there being nowhere else to put a special case.
/// </remarks>
internal static class ModelCommand
{
    public static int Run(string? homePath, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length == 0)
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        var home = EngramHome.ResolveFromProcess(homePath);

        return rest[0] switch
        {
            "list" => List(home, rest[1..], stdout, stderr),
            "install" => Install(home, rest[1..], stdout, stderr),
            "path" => PrintPath(home, rest[1..], stdout, stderr),
            _ => Unknown(rest[0], stderr),
        };
    }

    private static int Unknown(string subcommand, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown model subcommand '{subcommand}'");
        CliApp.PrintUsage(stderr);
        return 1;
    }

    private static int List(EngramHome home, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 0)
        {
            CliApp.PrintUsage(stderr);
            return 1;
        }

        foreach (var model in EmbeddingModels.All)
        {
            var installed = ModelFetcher.IsInstalled(home, model);
            var marks = new List<string> { installed ? "installed" : "not installed" };
            if (model.Id == EmbeddingModels.DefaultId)
            {
                marks.Add("default");
            }

            stdout.WriteLine($"{model.Id}  [{string.Join(", ", marks)}]");
            stdout.WriteLine(
                $"  {model.DisplayName} · {model.Dimensions}d · {model.SizeLabel} · "
                + $"{model.ContextTokens} tokens · {model.Languages}");
            stdout.WriteLine($"  {model.Tradeoff}");
            stdout.WriteLine();
        }

        stdout.WriteLine("Install one with: engram model install <id>");
        return 0;
    }

    private static int PrintPath(EngramHome home, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 1)
        {
            stderr.WriteLine("error: model path requires exactly one model id");
            return 1;
        }

        if (EmbeddingModels.Find(rest[0]) is not { } model)
        {
            return UnknownModel(rest[0], stderr);
        }

        stdout.WriteLine(ModelFetcher.PathFor(home, model));
        return 0;
    }

    private static int Install(EngramHome home, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Length != 1)
        {
            stderr.WriteLine("error: model install requires exactly one model id, or 'default'");
            stderr.WriteLine("run 'engram model list' to see the options");
            return 1;
        }

        var id = rest[0] == "default" ? EmbeddingModels.DefaultId : rest[0];
        if (EmbeddingModels.Find(id) is not { } model)
        {
            return UnknownModel(id, stderr);
        }

        stdout.WriteLine($"{model.DisplayName} — {model.SizeLabel}, {model.Dimensions} dimensions");
        stdout.WriteLine($"  from {model.Source!.Url}");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var result = ModelFetcher
            .EnsureAsync(home, model, client, Environment.GetEnvironmentVariable, new Reporter(stdout))
            .GetAwaiter()
            .GetResult();

        if (result.Outcome == FetchOutcome.Downloaded)
        {
            // The progress line is rewritten in place, so the next write needs its own line.
            stdout.WriteLine();
        }

        if (!result.Usable)
        {
            stderr.WriteLine(result.Message);
            return 1;
        }

        stdout.WriteLine(result.Message);
        stdout.WriteLine($"  {result.Path}");
        stdout.WriteLine();
        stdout.WriteLine("Then set this in the [embedding] section of your config:");
        stdout.WriteLine("  provider = \"local\"");
        stdout.WriteLine($"  model = \"{model.Id}\"");
        return 0;
    }

    private static int UnknownModel(string id, TextWriter stderr)
    {
        stderr.WriteLine($"error: no model named '{id}'");
        stderr.WriteLine($"known models: {string.Join(", ", EmbeddingModels.All.Select(m => m.Id))}");
        return 1;
    }

    /// <summary>
    /// Progress on one rewritten line, throttled to whole percents.
    /// </summary>
    /// <remarks>
    /// <para>A report per 80 KB chunk is roughly eight thousand of them for the largest rung, and
    /// a line of output each would cost more than the download.</para>
    ///
    /// <para><b>Not <see cref="Progress{T}"/>.</b> That type posts each report to the thread pool
    /// when there is no synchronisation context, so reports arrive out of order and the bar
    /// visibly counts backwards — observed as <c>82% … 81% … 82%</c> on a real download. Writing
    /// inline on the download's own thread keeps them ordered, and the write is a buffered
    /// console write, not something worth moving off the path.</para>
    /// </remarks>
    private sealed class Reporter(TextWriter stdout) : IProgress<FetchProgress>
    {
        private int lastPercent = -1;

        public void Report(FetchProgress value)
        {
            if (value.Fraction is not { } fraction)
            {
                return;
            }

            var percent = (int)(fraction * 100);
            if (percent == lastPercent)
            {
                return;
            }

            lastPercent = percent;
            stdout.Write($"\r  {percent,3}%  {value.Downloaded / 1_000_000d:0} MB");
            stdout.Flush();
        }
    }
}
