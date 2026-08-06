using Engram.Core;

namespace Engram.Core.Tests;

public class EmbeddingSettingsTests
{
    private static EmbeddingSettings Read(string toml) =>
        EmbeddingSettings.Read(ConfigFile.Parse(toml));

    [Fact]
    public void TheShippedDefault_IsOffAndClean()
    {
        var settings = EmbeddingSettings.Read(ConfigFile.Parse(DefaultConfig.Content));

        Assert.Equal(EmbeddingProvider.None, settings.Provider);
        Assert.Empty(settings.Problems);
        Assert.False(settings.IsUsable);
        Assert.Equal(16, settings.MaxBatch);
    }

    [Fact]
    public void AnAbsentConfig_IsOffAndClean()
    {
        var settings = EmbeddingSettings.Read(ConfigFile.Empty);

        Assert.Equal(EmbeddingProvider.None, settings.Provider);
        Assert.Empty(settings.Problems);
    }

    [Fact]
    public void ALocalModel_TakesItsWidthFromTheRegistry()
    {
        var settings = Read(
            """
            [embedding]
            provider = "local"
            model = "qwen3-embedding-0.6b"
            """);

        Assert.Empty(settings.Problems);
        Assert.Equal(1024, settings.Dimensions);
        Assert.Equal(new EmbeddingSpace("qwen3-embedding-0.6b", 1024), settings.Space);
    }

    /// <summary>
    /// Changing the model and forgetting the dimension would build an index of the wrong shape
    /// and fail at the first insert, a long way from the line that caused it.
    /// </summary>
    [Fact]
    public void ALocalModelWithAContradictoryDim_IsReported()
    {
        var settings = Read(
            """
            [embedding]
            provider = "local"
            model = "all-minilm-l6-v2"
            dim = 1024
            """);

        Assert.Contains(settings.Problems, p => p.Contains("contradicts", StringComparison.Ordinal));
        Assert.False(settings.IsUsable);
    }

    [Fact]
    public void AnUnknownLocalModel_ListsTheOnesThatExist()
    {
        var settings = Read(
            """
            [embedding]
            provider = "local"
            model = "some-model-nobody-has"
            """);

        var problem = Assert.Single(settings.Problems);
        foreach (var model in EmbeddingModels.All)
        {
            Assert.Contains(model.Id, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnHttpProvider_NeedsAnEndpointAModelAndAWidth()
    {
        var settings = Read("[embedding]\nprovider = \"openai-compat\"");

        Assert.Equal(3, settings.Problems.Count);
        Assert.Contains(settings.Problems, p => p.Contains("endpoint", StringComparison.Ordinal));
        Assert.Contains(settings.Problems, p => p.Contains("model name", StringComparison.Ordinal));
        Assert.Contains(settings.Problems, p => p.Contains("dim", StringComparison.Ordinal));
    }

    [Fact]
    public void AFullyConfiguredHttpProvider_IsUsable()
    {
        var settings = Read(
            """
            [embedding]
            provider = "openai-compat"
            endpoint = "http://localhost:1234/v1"
            model = "nomic-embed-text-v1.5"
            dim = 768
            api_key_env = "OPENAI_API_KEY"
            timeout_seconds = 30
            """);

        Assert.Empty(settings.Problems);
        Assert.True(settings.IsUsable);
        Assert.Equal(new EmbeddingSpace("nomic-embed-text-v1.5", 768), settings.Space);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.Timeout);
        Assert.Equal("OPENAI_API_KEY", settings.ApiKeyEnvironmentVariable);
    }

    [Fact]
    public void ANonHttpEndpoint_IsReported()
    {
        var settings = Read(
            """
            [embedding]
            provider = "ollama"
            endpoint = "localhost:11434"
            model = "nomic"
            dim = 768
            """);

        Assert.Contains(settings.Problems, p => p.Contains("not an http", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownProvider_NamesTheOnesThatExist()
    {
        var settings = Read("[embedding]\nprovider = \"anthropic\"");

        var problem = Assert.Single(settings.Problems);
        Assert.Contains("none, local, openai-compat, ollama", problem, StringComparison.Ordinal);
        Assert.Equal(EmbeddingProvider.None, settings.Provider);
    }

    [Theory]
    [InlineData("max_batch = 0")]
    [InlineData("max_batch = -4")]
    [InlineData("timeout_seconds = 0")]
    public void NonsensicalNumbers_AreReportedAndReplacedWithTheDefault(string line)
    {
        var settings = Read($"[embedding]\nprovider = \"none\"\n{line}");

        Assert.Single(settings.Problems);
        Assert.Equal(EmbeddingSettings.DefaultMaxBatch, settings.MaxBatch);
        Assert.Equal(TimeSpan.FromSeconds(EmbeddingSettings.DefaultTimeoutSeconds), settings.Timeout);
    }

    [Fact]
    public void ProviderNamesAreCaseInsensitive()
    {
        Assert.Equal(EmbeddingProvider.Ollama, Read("[embedding]\nprovider = \"Ollama\"").Provider);
    }

    /// <summary>
    /// A misconfigured vector lane must leave Engram working lexically and able to say why, not
    /// refuse to start (D18).
    /// </summary>
    [Fact]
    public void MisconfigurationNeverThrows()
    {
        var settings = Read(
            """
            [embedding]
            provider = "openai-compat"
            endpoint = "not a url at all"
            dim = -1
            """);

        Assert.NotEmpty(settings.Problems);
        Assert.False(settings.IsUsable);
        Assert.Null(settings.Space);
    }
}
