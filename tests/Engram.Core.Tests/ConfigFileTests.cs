using Engram.Core;

namespace Engram.Core.Tests;

public class ConfigFileTests
{
    [Fact]
    public void ReadsSectionsAndScalars()
    {
        var config = ConfigFile.Parse(
            """
            [embedding]
            provider = "ollama"
            dim = 768
            enabled = true

            [retrieval]
            seed_k = 32
            """);

        Assert.Equal("ollama", config.String("embedding", "provider"));
        Assert.Equal(768, config.Int("embedding", "dim"));
        Assert.True(config.Bool("embedding", "enabled"));
        Assert.Equal(32, config.Int("retrieval", "seed_k"));
        Assert.Empty(config.Errors);
    }

    [Fact]
    public void KeysAreScopedToTheirSection()
    {
        var config = ConfigFile.Parse(
            """
            [embedding]
            model = "nomic"

            [impressions]
            model = "qwen3-4b"
            """);

        Assert.Equal("nomic", config.String("embedding", "model"));
        Assert.Equal("qwen3-4b", config.String("impressions", "model"));
    }

    [Fact]
    public void DropsTrailingComments()
    {
        var config = ConfigFile.Parse(
            """
            [embedding]
            max_batch = 16      # bounded by D4
            """);

        Assert.Equal(16, config.Int("embedding", "max_batch"));
    }

    /// <summary>
    /// Endpoints carry fragments and API keys carry anything, so a <c>#</c> inside quotes is
    /// part of the value.
    /// </summary>
    [Fact]
    public void DoesNotTreatAHashInsideQuotesAsAComment()
    {
        var config = ConfigFile.Parse(
            """
            [embedding]
            endpoint = "http://localhost:1234/v1#alt"
            """);

        Assert.Equal("http://localhost:1234/v1#alt", config.String("embedding", "endpoint"));
    }

    [Fact]
    public void ReadsStringArrays()
    {
        var config = ConfigFile.Parse(
            """
            [taxonomy]
            roots = ["/knowledge", "/user", "/sessions"]
            """);

        Assert.Equal(["/knowledge", "/user", "/sessions"], config.Strings("taxonomy", "roots"));
    }

    [Fact]
    public void AbsentKeysAreNullRatherThanAnError()
    {
        var config = ConfigFile.Parse("[embedding]\nprovider = \"none\"");

        Assert.Null(config.String("embedding", "endpoint"));
        Assert.Null(config.Int("embedding", "dim"));
        Assert.Null(config.Bool("embedding", "missing"));
        Assert.Empty(config.Strings("embedding", "missing"));
    }

    /// <summary>
    /// An unknown key is how a config file survives a version bump and how a user leaves
    /// themselves a note. It must not be an error.
    /// </summary>
    [Fact]
    public void UnknownKeysAreKeptAndReportNothing()
    {
        var config = ConfigFile.Parse(
            """
            [embedding]
            provider = "none"
            some_future_key = "value"
            """);

        Assert.Equal("value", config.String("embedding", "some_future_key"));
        Assert.Empty(config.Errors);
    }

    [Fact]
    public void MalformedLinesAreReportedRatherThanIgnored()
    {
        var config = ConfigFile.Parse(
            """
            [embedding]
            this line has no equals sign
            [unterminated
            """);

        Assert.Equal(2, config.Errors.Count);
        Assert.Contains(config.Errors, e => e.Line == 2);
        Assert.Contains(config.Errors, e => e.Line == 3);
    }

    [Fact]
    public void ADuplicatedKeyTakesTheLaterValue()
    {
        var config = ConfigFile.Parse(
            """
            [embedding]
            dim = 384
            dim = 768
            """);

        Assert.Equal(768, config.Int("embedding", "dim"));
    }

    [Fact]
    public void UnquotedValuesAreReadAsStrings()
    {
        var config = ConfigFile.Parse("[embedding]\nprovider = ollama");

        Assert.Equal("ollama", config.String("embedding", "provider"));
    }

    [Fact]
    public void AMissingFileParsesAsEmptyRatherThanFailing()
    {
        // Every setting has a default, so an instance that was never configured must behave
        // exactly like one configured with the defaults.
        var config = ConfigFile.Load(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.toml"));

        Assert.Null(config.String("embedding", "provider"));
        Assert.Empty(config.Errors);
    }

    [Fact]
    public void TheShippedDefaultConfigParsesCleanly()
    {
        // The file `init` writes is the one most instances will ever have. If it does not read
        // back, every setting in it is silently a default.
        var config = ConfigFile.Parse(DefaultConfig.Content);

        Assert.Empty(config.Errors);
        Assert.Equal("none", config.String("embedding", "provider"));
        Assert.Equal(16, config.Int("embedding", "max_batch"));
        Assert.Equal(500, config.Int("retrieval", "default_budget_tokens"));
        Assert.Equal("extractive", config.String("impressions", "mode"));
        Assert.Contains("/knowledge", config.Strings("taxonomy", "roots"));
    }

    /// <summary>
    /// A list a person is expected to edit cannot live on one line — twenty ignore patterns on
    /// one line is a line nobody reads, let alone changes.
    /// </summary>
    [Fact]
    public void Strings_ReadsAnArrayThatSpansSeveralLines()
    {
        var config = ConfigFile.Parse(
            """
            [indexing]
            ignore = [
              "**/bin/**",
              "**/obj/**",
              "**/node_modules/**",
            ]
            max_file_bytes = 4096
            """);

        Assert.Equal(["**/bin/**", "**/obj/**", "**/node_modules/**"], config.Strings("indexing", "ignore"));
    }

    // The key after a multi-line array has to still be found, or joining the array would swallow
    // the rest of the section.
    [Fact]
    public void AKeyAfterAMultiLineArray_IsStillRead()
    {
        var config = ConfigFile.Parse(
            """
            [indexing]
            ignore = [
              "a",
              "b",
            ]
            max_file_bytes = 4096
            """);

        Assert.Equal(4096, config.Int("indexing", "max_file_bytes"));
    }

    [Fact]
    public void ASectionAfterAMultiLineArray_IsStillRead()
    {
        var config = ConfigFile.Parse(
            """
            [indexing]
            ignore = [
              "a",
            ]

            [embedding]
            provider = "ollama"
            """);

        Assert.Equal("ollama", config.String("embedding", "provider"));
    }

    [Fact]
    public void CommentsInsideAMultiLineArray_AreDropped()
    {
        var config = ConfigFile.Parse(
            """
            [indexing]
            ignore = [
              "a",   # the first one
              # a whole line of commentary
              "b",
            ]
            """);

        Assert.Equal(["a", "b"], config.Strings("indexing", "ignore"));
    }

    [Fact]
    public void ASingleLineArray_StillReads()
    {
        var config = ConfigFile.Parse("[indexing]\nignore = [\"a\", \"b\"]\n");

        Assert.Equal(["a", "b"], config.Strings("indexing", "ignore"));
    }

    // Better to say so than to silently swallow the rest of the file into an array that never
    // ends.
    [Fact]
    public void AnUnclosedArray_IsAnError()
    {
        var config = ConfigFile.Parse("[indexing]\nignore = [\n  \"a\",\n");

        Assert.Contains(config.Errors, e => e.Problem.Contains("never closed", StringComparison.Ordinal));
    }
}
