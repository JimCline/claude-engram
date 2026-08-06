using Engram.Cli;
using Engram.Core;

namespace Engram.Integration.Tests;

/// <summary>
/// The picker against a real config file: what it writes, and what it refuses to.
/// </summary>
/// <remarks>
/// Nothing here chooses <c>local</c>, because that rung downloads a model. The download is covered
/// by the model command's own tests; what is unproven and worth proving is the config edit.
/// </remarks>
public sealed class EmbeddingSetupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static (int Code, string Out, string Error) Run(EngramHome home, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CliApp.Run([.. new[] { "--home", home.Root }, .. args], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string? Setting(EngramHome home, string key) =>
        ConfigEditor.Read(File.ReadAllText(home.ConfigPath), "embedding", key);

    /// <summary>
    /// Edits the config the way a person would, by replacing text. Deliberately not via
    /// <see cref="ConfigEditor.Set"/>, which stamps the line as Engram's own — a fixture built out
    /// of that would be testing whether the tool respects itself.
    /// </summary>
    private static void HandEdit(EngramHome home, string from, string to)
    {
        var text = File.ReadAllText(home.ConfigPath);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(home.ConfigPath, text.Replace(from, to, StringComparison.Ordinal));
    }

    [Fact]
    public void Init_WithoutTheFlag_WritesNoEmbeddingSettings()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var result = Run(sandbox.Home, "init");

        Assert.Equal(0, result.Code);
        Assert.Equal("\"none\"", Setting(sandbox.Home, "provider"));
        Assert.DoesNotContain("vector lane", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithEmbeddings_AndNoTerminal_ShowsTheRungsAndChangesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        var before = File.Exists(sandbox.Home.ConfigPath) ? File.ReadAllText(sandbox.Home.ConfigPath) : null;

        var result = Run(sandbox.Home, "init", "--with-embeddings");

        Assert.Equal(0, result.Code);
        Assert.Contains("lexical recall only", result.Out, StringComparison.Ordinal);
        Assert.Contains(EmbeddingModels.DefaultId, result.Out, StringComparison.Ordinal);
        Assert.Contains("Not a terminal", result.Out, StringComparison.Ordinal);
        Assert.Equal("\"none\"", Setting(sandbox.Home, "provider"));
        _ = before;
    }

    [Fact]
    public void TheRungs_NameEveryModelEngramCanRunItself()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var result = Run(sandbox.Home, "init", "--with-embeddings");

        foreach (var model in EmbeddingModels.All)
        {
            Assert.Contains(model.Id, result.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Init_WithAnEndpoint_WritesEveryKeyItNeeds()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var result = Run(
            sandbox.Home,
            "init",
            "--provider", "openai-compat",
            "--endpoint", "http://localhost:1234/v1",
            "--model", "nomic-embed-text-v1.5",
            "--dim", "768",
            "--api-key-env", "MY_KEY");

        Assert.Equal(0, result.Code);
        Assert.Equal("\"openai-compat\"", Setting(sandbox.Home, "provider"));
        Assert.Equal("\"http://localhost:1234/v1\"", Setting(sandbox.Home, "endpoint"));
        Assert.Equal("768", Setting(sandbox.Home, "dim"));
        Assert.Equal("\"MY_KEY\"", Setting(sandbox.Home, "api_key_env"));
    }

    [Fact]
    public void WhatItWrites_IsWhatTheSettingsReaderThenReads()
    {
        using var sandbox = new SandboxHome(initialize: false);

        Run(sandbox.Home, "init", "--provider", "ollama", "--endpoint", "http://localhost:11434", "--dim", "768");

        var settings = EmbeddingSettings.Read(ConfigFile.Load(sandbox.Home.ConfigPath));

        Assert.Equal(EmbeddingProvider.Ollama, settings.Provider);
        Assert.Equal("http://localhost:11434", settings.Endpoint);
        Assert.Equal(768, settings.Dimensions);
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void Init_BacksUpTheConfigBeforeChangingIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Run(sandbox.Home, "init");
        var original = File.ReadAllText(sandbox.Home.ConfigPath);

        var result = Run(sandbox.Home, "init", "--provider", "ollama", "--endpoint", "http://x", "--dim", "768");

        var backup = Assert.Single(Directory.GetFiles(sandbox.Home.Root, "config.toml.bak-*"));
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Contains("Backed up", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_AskingForWhatTheConfigAlreadySays_TouchesNothing()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Run(sandbox.Home, "init");

        var result = Run(sandbox.Home, "init", "--provider", "none");

        Assert.Equal(0, result.Code);
        Assert.Contains("already says that", result.Out, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(sandbox.Home.Root, "config.toml.bak-*"));
    }

    [Fact]
    public void Init_AfterItsOwnEarlierEdit_ChangesTheChoiceRatherThanRefusingIt()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Run(sandbox.Home, "init", "--provider", "ollama", "--endpoint", "http://x", "--dim", "768");

        var result = Run(sandbox.Home, "init", "--provider", "none");

        Assert.Equal(0, result.Code);
        Assert.Equal("\"none\"", Setting(sandbox.Home, "provider"));
    }

    [Fact]
    public void Init_RefusesToOverwriteAValueTheUserSetThemselves()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Run(sandbox.Home, "init");
        HandEdit(sandbox.Home, "provider = \"none\"", "provider = \"ollama\"");

        var result = Run(sandbox.Home, "init", "--provider", "none");

        Assert.Equal(1, result.Code);
        Assert.Contains("Refusing to overwrite", result.Error, StringComparison.Ordinal);
        Assert.Equal("\"ollama\"", Setting(sandbox.Home, "provider"));
    }

    [Fact]
    public void Init_WithForce_OverwritesItAnyway()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Run(sandbox.Home, "init");
        HandEdit(sandbox.Home, "provider = \"none\"", "provider = \"ollama\"");

        var result = Run(sandbox.Home, "init", "--provider", "none", "--force");

        Assert.Equal(0, result.Code);
        Assert.Equal("\"none\"", Setting(sandbox.Home, "provider"));
    }

    [Fact]
    public void Init_RunTwiceWithTheSameChoice_ChangesNothingTheSecondTime()
    {
        using var sandbox = new SandboxHome(initialize: false);
        Run(sandbox.Home, "init", "--provider", "openai-compat", "--endpoint", "http://x", "--dim", "768");

        var result = Run(sandbox.Home, "init", "--provider", "openai-compat", "--endpoint", "http://x", "--dim", "768");

        Assert.Equal(0, result.Code);
        Assert.Contains("already says that", result.Out, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(sandbox.Home.Root, "config.toml.bak-*"));
    }

    [Fact]
    public void Init_KeepsTheCommentsThatExplainTheChoice()
    {
        using var sandbox = new SandboxHome(initialize: false);

        Run(sandbox.Home, "init", "--provider", "none");

        var text = File.ReadAllText(sandbox.Home.ConfigPath);
        Assert.Contains("Anthropic publishes no embeddings API", text, StringComparison.Ordinal);
        Assert.Contains("lexical recall only", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithABadDimension_SaysSoRatherThanWritingIt()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var result = Run(sandbox.Home, "init", "--provider", "openai-compat", "--endpoint", "http://x", "--dim", "wide");

        Assert.Equal(1, result.Code);
        Assert.Contains("--dim must be a positive number", result.Error, StringComparison.Ordinal);
        Assert.Equal("\"none\"", Setting(sandbox.Home, "provider"));
    }

    [Fact]
    public void Init_WithAnUnknownLocalModel_SaysSoBeforeDownloadingAnything()
    {
        using var sandbox = new SandboxHome(initialize: false);

        var result = Run(sandbox.Home, "init", "--provider", "local", "--model", "not-a-model");

        Assert.Equal(1, result.Code);
        Assert.Contains("no local model called", result.Error, StringComparison.Ordinal);
        Assert.Equal("\"none\"", Setting(sandbox.Home, "provider"));
    }

    [Fact]
    public void Ask_ChoosingNone_IsARealAnswer()
    {
        var choice = EmbeddingSetup.Ask(new StringReader("1\n"), new StringWriter());

        Assert.Equal("none", choice!.Provider);
        Assert.Null(choice.Model);
    }

    [Fact]
    public void Ask_ChoosingLocalWithoutNamingAModel_TakesTheDefault()
    {
        var choice = EmbeddingSetup.Ask(new StringReader("2\n\n"), new StringWriter());

        Assert.Equal("local", choice!.Provider);
        Assert.Equal(EmbeddingModels.DefaultId, choice.Model);
    }

    [Fact]
    public void Ask_ChoosingOllama_RecordsTheNativeProviderRatherThanTheCompatibleOne()
    {
        var choice = EmbeddingSetup.Ask(
            new StringReader("3\nhttp://localhost:11434\ny\nnomic-embed-text\n768\n\n"),
            new StringWriter());

        Assert.Equal("ollama", choice!.Provider);
        Assert.Equal("http://localhost:11434", choice.Endpoint);
        Assert.Equal(768, choice.Dimensions);
        Assert.Null(choice.ApiKeyEnvironmentVariable);
    }

    [Fact]
    public void Ask_WithAnEndpointThatWillNotSayItsWidth_BacksOutRatherThanGuessing()
    {
        // A wrong width does not fail loudly; it produces vectors that never match anything.
        var choice = EmbeddingSetup.Ask(
            new StringReader("3\nhttp://localhost:1234/v1\nn\nsome-model\n\n\n"),
            new StringWriter());

        Assert.Null(choice);
    }

    [Fact]
    public void Ask_WithABlankAnswer_LeavesTheConfigAlone()
    {
        Assert.Null(EmbeddingSetup.Ask(new StringReader("\n"), new StringWriter()));
    }

    [Fact]
    public void Ask_AtEndOfInput_LeavesTheConfigAlone()
    {
        Assert.Null(EmbeddingSetup.Ask(new StringReader(string.Empty), new StringWriter()));
    }

    [Fact]
    public void KeysFor_None_WritesOnlyTheProvider()
    {
        var keys = EmbeddingSetup.KeysFor(new EmbeddingChoice("none", null, null, null, null));

        var key = Assert.Single(keys);
        Assert.Equal("provider", key.Key);
        Assert.Equal("\"none\"", key.Value);
    }
}
