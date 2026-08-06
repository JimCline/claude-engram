using Engram.Core;

namespace Engram.Core.Tests;

public class EmbedderFactoryTests
{
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    private static EmbedderResolution Resolve(string toml, Func<string, string?>? environment = null) =>
        EmbedderFactory.Create(
            EmbeddingSettings.Read(ConfigFile.Parse(toml)), environment ?? NoEnvironment);

    /// <summary>
    /// Absence, not a null object, is how embeddings-off is represented (D18). A provider that
    /// returned empty vectors would let a disabled install write rows that rank like noise.
    /// </summary>
    [Fact]
    public void ProviderNone_ResolvesToNothingAndSaysSoWithoutAlarm()
    {
        var resolution = Resolve("[embedding]\nprovider = \"none\"");

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Embedder);
        Assert.Contains("supported configuration", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredEndpoint_ResolvesToAnHttpEmbedder()
    {
        var resolution = Resolve(
            """
            [embedding]
            provider = "openai-compat"
            endpoint = "http://localhost:1234/v1"
            model = "nomic-embed-text-v1.5"
            dim = 768
            """);

        using var embedder = resolution.Embedder as IDisposable;
        Assert.IsType<OpenAiCompatibleEmbedder>(resolution.Embedder);
        Assert.Equal(new EmbeddingSpace("nomic-embed-text-v1.5", 768), resolution.Embedder!.Space);
    }

    [Fact]
    public void Ollama_ResolvesToItsNativeClient()
    {
        var resolution = Resolve(
            """
            [embedding]
            provider = "ollama"
            endpoint = "http://localhost:11434"
            model = "nomic-embed-text"
            dim = 768
            """);

        using var embedder = resolution.Embedder as IDisposable;
        Assert.IsType<OllamaEmbedder>(resolution.Embedder);
    }

    [Fact]
    public void MisconfigurationBecomesTheReason()
    {
        var resolution = Resolve("[embedding]\nprovider = \"ollama\"");

        Assert.False(resolution.Resolved);
        Assert.Contains("endpoint", resolution.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local runtime ignores the header, so demanding a key there would turn a working setup
    /// into a broken one over a setting that does nothing.
    /// </summary>
    [Fact]
    public void AnEmptyKeyForALocalEndpoint_IsNotAProblem()
    {
        var resolution = Resolve(
            """
            [embedding]
            provider = "openai-compat"
            endpoint = "http://localhost:1234/v1"
            model = "nomic"
            dim = 768
            api_key_env = "SOME_KEY_THAT_IS_NOT_SET"
            """);

        using var embedder = resolution.Embedder as IDisposable;
        Assert.True(resolution.Resolved);
    }

    [Fact]
    public void AnEmptyKeyForARemoteEndpoint_IsReportedRatherThanSentEmpty()
    {
        var resolution = Resolve(
            """
            [embedding]
            provider = "openai-compat"
            endpoint = "https://api.openai.com/v1"
            model = "text-embedding-3-small"
            dim = 1536
            api_key_env = "OPENAI_API_KEY"
            """);

        Assert.False(resolution.Resolved);
        Assert.Contains("OPENAI_API_KEY", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARemoteEndpointWithAKeyPresent_Resolves()
    {
        var resolution = Resolve(
            """
            [embedding]
            provider = "openai-compat"
            endpoint = "https://api.openai.com/v1"
            model = "text-embedding-3-small"
            dim = 1536
            api_key_env = "OPENAI_API_KEY"
            """,
            name => name == "OPENAI_API_KEY" ? "sk-test" : null);

        using var embedder = resolution.Embedder as IDisposable;
        Assert.True(resolution.Resolved);
    }

    /// <summary>
    /// Until there is a runtime to run them, saying so beats resolving to something that fails
    /// on first use with a stack trace instead of a sentence.
    /// </summary>
    [Fact]
    public void ProviderLocal_SaysItIsNotWiredUpYet()
    {
        var resolution = Resolve(
            """
            [embedding]
            provider = "local"
            model = "nomic-embed-text-v1.5"
            """);

        Assert.False(resolution.Resolved);
        Assert.Contains("ollama", resolution.Reason, StringComparison.Ordinal);
    }
}
