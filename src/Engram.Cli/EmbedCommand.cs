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
    /// <summary>
    /// Prints the report once, or redraws it until interrupted with <c>--watch</c>.
    /// </summary>
    /// <remarks>
    /// <para>Watching is the only thing here that redraws, and it goes through <see cref="Tui"/>
    /// rather than writing escapes of its own — the row budget is the same rule D52 was paid for,
    /// and a second implementation of it would drift the first time either changed. A block whose
    /// height varies between frames would also break the arithmetic, so the block is padded to a
    /// fixed height rather than trusting successive reports to be the same length.</para>
    ///
    /// <para>Not interactive means not redrawing: a pipe gets one report and exits even with
    /// <c>--watch</c>, because a loop nobody can interrupt writing frames into a file is a way to
    /// fill a disk, not a feature.</para>
    /// </remarks>
    private static int Status(string? homePath, string[] args, TextWriter stdout)
    {
        var home = EngramHome.ResolveFromProcess(homePath);
        var tui = Tui.Detect();

        if (!args.Contains("--watch") || !tui.Interactive)
        {
            foreach (var line in EmbedStatus.Lines(
                EmbedStatus.Read(home, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, tui.Interactive))
            {
                stdout.WriteLine(line);
            }

            return 0;
        }

        var height = 0;
        var rows = 0;

        stdout.Write("\x1b[?25l");
        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                var view = EmbedStatus.Read(home, now);
                var lines = new List<string>(EmbedStatus.Lines(view, now, decorated: true));

                // The tallest frame so far sets the height for every frame after it. Letting the
                // block shrink would leave the rows it no longer writes on screen, below a cursor
                // that has already moved back above them.
                height = Math.Max(height, lines.Count);
                while (lines.Count < height)
                {
                    lines.Add(string.Empty);
                }

                rows = tui.Frame(stdout, lines, rows);

                if (view.Pending == 0 && view.Total > 0)
                {
                    return 0;
                }

                Thread.Sleep(EmbedStatus.WatchInterval);
            }
        }
        finally
        {
            stdout.Write("\x1b[?25h");
            stdout.Flush();
        }
    }

    public static int Run(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Contains("--status"))
        {
            return Status(homePath, args, stdout);
        }

        if (args.Contains("--rebuild"))
        {
            return Rebuild(homePath, args, stdout, stderr);
        }

        if (!args.Contains("--probe"))
        {
            stderr.WriteLine("error: engram embed needs --status, --probe or --rebuild.");
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

    /// <summary>
    /// <c>engram embed --rebuild</c> — discard the vector index and make it again from
    /// <c>fact</c>. Dry run unless <c>--apply</c> is given.
    /// </summary>
    /// <remarks>
    /// Refuses while the server is up, which is the one thing that makes this command correct
    /// rather than merely present. <see cref="EmbeddingBacklog"/> is the single owner of vector
    /// production, and the server holds an embedder built at its startup — so a rebuild prompted
    /// by a config change would have that older embedder racing this one to refill the same rows,
    /// and worse, its <c>EnsureCreated</c> would re-pin the table to the space the user just
    /// moved away from. Stopping first is not politeness about a lock; it is the only way the new
    /// space wins.
    /// </remarks>
    private static int Rebuild(string? homePath, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var home = EngramHome.ResolveFromProcess(homePath);
        if (!File.Exists(home.DatabasePath))
        {
            stderr.WriteLine($"error: no store at {home.DatabasePath} — run 'engram init' first");
            return 1;
        }

        var settings = EmbeddingSettings.Read(ConfigFile.Load(home.ConfigPath));
        if (settings.Provider is EmbeddingProvider.None)
        {
            stderr.WriteLine("error: provider = \"none\", so there is no index to rebuild.");
            stderr.WriteLine("       Run 'engram init --with-embeddings' to choose one.");
            return 1;
        }

        var lifecycle = new ServerLifecycle(
            new ProcessInspector(),
            new HttpServerHealthChecker(),
            new ProcessServerLauncher());

        if (lifecycle.Status(
                home,
                EngramVersion.Current,
                ServerLifecycleTimeouts.Default.HealthCheckTimeout)
            .ServerIsAlive)
        {
            stderr.WriteLine("error: the server is running, and it embeds from the model it loaded at startup.");
            stderr.WriteLine("       Rebuilding underneath it would refill the index in the old space.");
            stderr.WriteLine("       Run 'engram stop' first, then rebuild, then 'engram start'.");
            return 1;
        }

        using var connection = EngramDatabase.OpenInitialized(home);

        if (VectorExtension.Load(connection, home.LibDir) is not VectorExtensionState.Loaded and var state)
        {
            stderr.WriteLine(state == VectorExtensionState.NotInstalled
                ? $"error: sqlite-vec is not in {home.LibDir}, so there is no index to rebuild."
                : $"error: sqlite-vec is in {home.LibDir} and would not load — wrong architecture, or truncated.");
            return 1;
        }

        // Owned here and disposed here (D35). The factory attaches to this runtime and never
        // launches one, so whoever creates it is the only thing that can stop it — and under
        // provider = "local" a rebuild is exactly the caller that should pay to start a model.
        using var local = new LocalRuntime(home);
        var resolution = EmbedderFactory.Create(settings, Environment.GetEnvironmentVariable, client: null, local);
        using var owned = resolution.Embedder as IDisposable;

        if (resolution.Embedder is not { } embedder)
        {
            stderr.WriteLine($"error: {resolution.Reason}");
            return 1;
        }

        var plan = VectorRebuild.Plan(connection, embedder.Space);
        WritePlan(plan, stdout);

        if (!args.Contains("--apply"))
        {
            stdout.WriteLine();
            stdout.WriteLine($"Dry run only — nothing was changed. Re-run with --apply to spend {plan.ToEmbed} embedder call(s).");
            return 0;
        }

        stdout.WriteLine();

        var result = VectorRebuild.RunAsync(
            connection,
            embedder,
            plan,
            settings.MaxBatch,
            pass => stdout.WriteLine($"  embedded {pass.Embedded}/{plan.ToEmbed}, {pass.Remaining} to go"))
            .GetAwaiter()
            .GetResult();

        stdout.WriteLine();
        stdout.WriteLine(result.Outcome switch
        {
            BackfillOutcome.Completed =>
                $"Rebuilt: {result.Embedded} vector(s) in {embedder.Space}"
                + (result.Failed > 0 ? $", {result.Failed} text(s) the embedder refused." : "."),
            BackfillOutcome.StalledOnFailures =>
                $"Stopped: {result.Embedded} embedded, then a whole batch failed. "
                + $"{result.Remaining} still pending — the endpoint is answering, but not for these.",
            BackfillOutcome.SpaceMismatch =>
                "Stopped: the index reports a different space than the embedder, immediately after "
                + "being rebuilt into it. That is a bug, not a configuration problem.",
            _ => $"Stopped: {result.Embedded} embedded, {result.Remaining} pending.",
        });

        return result.Outcome is BackfillOutcome.Completed ? 0 : 1;
    }

    private static void WritePlan(RebuildPlan plan, TextWriter stdout)
    {
        stdout.WriteLine(plan.Action switch
        {
            RebuildAction.Build => $"No index yet. Building one in {plan.Target}.",
            RebuildAction.Clear => $"Index holds {plan.Target} and stays; its {plan.Discarded} row(s) do not.",
            _ => $"Index holds {plan.Current?.ToString() ?? "an unrecorded space"} and must be recreated — {plan.Reason}.",
        });

        stdout.WriteLine();
        stdout.WriteLine($"  discard    {plan.Discarded} vector(s)");
        stdout.WriteLine($"  re-embed   {plan.ToEmbed} live fact(s) through {plan.Target}");
        stdout.WriteLine($"  input      {plan.TargetInput}");
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
