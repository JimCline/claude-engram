using System.Globalization;
using Engram.Core;

namespace Engram.Cli;

/// <summary>What the user chose, once flags and answers have both been read.</summary>
public sealed record EmbeddingChoice(
    string Provider,
    string? Model,
    string? Endpoint,
    int? Dimensions,
    string? ApiKeyEnvironmentVariable);

/// <summary>
/// The three rungs of the embedding ladder, and the config edit that lands on one.
/// </summary>
/// <remarks>
/// <para>This exists because <c>model install</c> used to end by printing two lines to paste into
/// a config file. Everything needed to write them was already in hand at that moment — the model
/// id, the provider it implies, the file's location. Telling someone to go and do it themselves
/// was the last manual step in an otherwise automatic setup, and the step where a typo produces a
/// silently lexical-only instance that looks configured.</para>
///
/// <para><b>"none" is presented first and is a real answer.</b> Recall works lexically without a
/// vector lane, and the lane costs disk, memory and startup. A picker that treats the largest
/// model as the obvious choice would be selling something.</para>
/// </remarks>
public static class EmbeddingSetup
{
    public const string Section = EmbeddingSettings.Section;

    public static void Describe(TextWriter stdout, EngramHome home)
    {
        ArgumentNullException.ThrowIfNull(stdout);

        stdout.WriteLine("Engram's vector lane is optional. Recall works lexically without it; turning it");
        stdout.WriteLine("on trades disk, memory and startup time for finding facts that share no words");
        stdout.WriteLine("with the query.");
        stdout.WriteLine();
        stdout.WriteLine("  1  none            lexical recall only — nothing to download, nothing to run");
        // The path on its own line, the way option 3's second line already reads. Inline it runs
        // to ~96 columns for an ordinary home and wraps mid-sentence. Nothing here redraws, so
        // this is legibility rather than the row-budget rule Tui enforces.
        stdout.WriteLine("  2  local           Engram runs the model itself, from a file in");
        stdout.WriteLine("                     " + home.ModelsDir);

        foreach (var model in EmbeddingModels.All)
        {
            stdout.WriteLine(
                "       " + model.Id.PadRight(22)
                    + (model.Dimensions.ToString(CultureInfo.InvariantCulture) + "d").PadLeft(6)
                    + model.SizeLabel.PadLeft(9)
                    + "   " + Window(model.ContextTokens) + " window, " + model.Languages);
        }

        stdout.WriteLine("  3  endpoint        something you already run answers POST /v1/embeddings");
        stdout.WriteLine("                     LM Studio, llama.cpp, vLLM, Ollama, or a hosted API");
        stdout.WriteLine();
        stdout.WriteLine("Anthropic publishes no embeddings API, so Claude cannot be the provider here.");
    }

    /// <summary>Reads the choice from an interactive terminal, or null if the user backed out.</summary>
    /// <param name="probe">
    /// Asks an endpoint its own vector width, given provider, endpoint and model. Injected rather
    /// than called directly so this stays a function of its input — and so a test does not have to
    /// stand up a server to prove the questions are asked in the right order.
    /// </param>
    /// <param name="tui">
    /// Presentation only. Null (and <see cref="Tui.Plain"/>) is the frozen line-prompt flow every
    /// test drives; a detected terminal gets arrow-key menus carrying each option's tradeoffs.
    /// One control flow either way, so the answers, defaults and validation cannot diverge by mode.
    /// </param>
    /// <summary>The local-model menu entries.</summary>
    /// <remarks>
    /// Named rather than inlined so a test can assert on the same list the picker draws. The
    /// split matters: the specs sit beside the label, and the tradeoff paragraph goes in Detail,
    /// which Tui gives a fixed block of its own. Concatenating the two is what made these
    /// entries ~290 characters and broke the redraw — clipping now stops that from corrupting
    /// the screen, but it would silently ellipse the specs instead, so keep them apart.
    /// </remarks>
    internal static IReadOnlyList<TuiChoice> ModelChoices() =>
        [.. EmbeddingModels.All.Select(m => new TuiChoice(
            m.Id,
            m.Id,
            $"{m.Dimensions}d · {m.SizeLabel.Trim()} · {Window(m.ContextTokens)} window · {m.Languages}",
            m.Tradeoff))];

    public static EmbeddingChoice? Ask(
        TextReader stdin,
        TextWriter stdout,
        Func<string, string, string, int?>? probe = null,
        Tui? tui = null)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        tui ??= Tui.Plain;

        var rung = tui.Menu(
            stdin,
            stdout,
            "Which one?",
            [
                new TuiChoice("none", "none", "lexical recall only — nothing to download, nothing to run"),
                new TuiChoice("local", "local", "Engram runs the model itself; the weights download once"),
                new TuiChoice("endpoint", "endpoint", "a server you already run answers POST /v1/embeddings"),
            ],
            "Which one? [1/2/3, or blank to leave it alone] ",
            static answer => answer switch
            {
                "1" or "none" => "none",
                "2" or "local" => "local",
                "3" or "endpoint" => "endpoint",
                _ => null,
            });

        switch (rung)
        {
            case "none":
                return new EmbeddingChoice("none", null, null, null, null);

            case "local":
            {
                var model = tui.Menu(
                    stdin,
                    stdout,
                    "Which model?",
                    ModelChoices(),
                    $"Which model? [blank for {EmbeddingModels.DefaultId}] ",
                    static id => string.IsNullOrEmpty(id) ? EmbeddingModels.DefaultId : id);
                return model is null || EmbeddingModels.Find(model) is null
                    ? null
                    : new EmbeddingChoice("local", model, null, null, null);
            }

            case "endpoint":
            {
                var endpoint = tui.Line(stdin, stdout, "Endpoint URL? [e.g. http://localhost:1234/v1] ");
                if (string.IsNullOrEmpty(endpoint))
                {
                    return null;
                }

                var provider = tui.Line(stdin, stdout, "Is that Ollama's native API? [y/N] ")
                    .StartsWith('y') ? "ollama" : "openai-compat";
                var model = tui.Line(stdin, stdout, "What does the endpoint call the model? ");

                // The endpoint is asked before the user is. A width is not knowable from a model
                // name — an endpoint may serve a quantized or truncated variant under the same
                // label — and getting it wrong does not fail loudly, it stores vectors that never
                // match. An observation beats both a lookup and a guess.
                if (model is { Length: > 0 } && probe?.Invoke(provider, endpoint, model) is { } measured)
                {
                    stdout.WriteLine($"  {endpoint} returns {measured} dimensions.");
                    var keyForMeasured = tui.Line(stdin, stdout, "Environment variable holding the API key, if any? ");
                    return new EmbeddingChoice(
                        provider,
                        model,
                        endpoint,
                        measured,
                        string.IsNullOrEmpty(keyForMeasured) ? null : keyForMeasured);
                }

                stdout.WriteLine("  Could not ask the endpoint, so this one has to be typed.");
                var width = tui.Line(stdin, stdout, "How many dimensions does it return? ");
                if (!int.TryParse(width, CultureInfo.InvariantCulture, out var dimensions) || dimensions < 1)
                {
                    return null;
                }

                var keyVariable = tui.Line(stdin, stdout, "Environment variable holding the API key, if any? ");

                return new EmbeddingChoice(
                    provider,
                    string.IsNullOrEmpty(model) ? null : model,
                    endpoint,
                    dimensions,
                    string.IsNullOrEmpty(keyVariable) ? null : keyVariable);
            }

            default:
                return null;
        }
    }

    /// <summary>Turns a choice into the exact set of config keys it implies.</summary>
    public static IReadOnlyList<(string Key, string Value)> KeysFor(EmbeddingChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        var keys = new List<(string, string)> { ("provider", ConfigEditor.Quote(choice.Provider)) };

        if (choice.Model is { Length: > 0 } model)
        {
            keys.Add(("model", ConfigEditor.Quote(model)));
        }

        if (choice.Endpoint is { Length: > 0 } endpoint)
        {
            keys.Add(("endpoint", ConfigEditor.Quote(endpoint)));
        }

        if (choice.Dimensions is { } dimensions)
        {
            keys.Add(("dim", ConfigEditor.Number(dimensions)));
        }

        if (choice.ApiKeyEnvironmentVariable is { Length: > 0 } keyVariable)
        {
            keys.Add(("api_key_env", ConfigEditor.Quote(keyVariable)));
        }

        return keys;
    }

    /// <summary>
    /// Writes the choice into the config, backing the file up first and refusing any key the user
    /// has already set to something of their own.
    /// </summary>
    public static int Apply(
        EngramHome home,
        EmbeddingChoice choice,
        bool force,
        DateTimeOffset now,
        TextWriter stdout,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(home);

        return ConfigWriter.Apply(home, Section, KeysFor(choice), force, now, stdout, stderr);
    }

    private static string Window(int tokens) => tokens >= 1024
        ? (tokens / 1024).ToString(CultureInfo.InvariantCulture) + "k-token"
        : tokens.ToString(CultureInfo.InvariantCulture) + "-token";
}
