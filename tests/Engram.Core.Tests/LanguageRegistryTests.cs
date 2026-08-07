using Engram.Core;

namespace Engram.Core.Tests;

public class LanguageRegistryTests
{
    /// <summary>
    /// The D24 conformance suite: one set of assertions, driven entirely by the registry.
    /// Adding a language must not require touching this file — the row carries its own
    /// fixture, and a row that ships without one is the first assertion's failure.
    /// </summary>
    [Fact]
    public void EveryRegisteredLanguage_ExtractsItsOwnFixture()
    {
        foreach (var language in LanguageRegistry.All)
        {
            Assert.NotNull(language.Fixture);

            var filePath = $"/projects/p/code/r/sample{language.Extensions[0]}";
            var candidates = CodeAnalyzer.Analyze(filePath, language.Fixture.Source, language);

            foreach (var symbol in language.Fixture.ExpectedSymbols)
            {
                var expected = language.DocHeadings
                    ? candidates.Any(c => c.Kind == "section" && c.EntityPath.EndsWith($"#{symbol}", StringComparison.Ordinal))
                    : candidates.Any(c => c.Kind == "symbol" && c.DisplayName == symbol);

                Assert.True(expected, $"{language.Id}: expected {symbol} extracted from the fixture, got: "
                    + string.Join(" | ", candidates.Select(c => $"{c.Kind} {c.EntityPath}")));
            }

            foreach (var import in language.Fixture.ExpectedImports)
            {
                Assert.True(
                    candidates.Any(c => c.Predicate == "imports" && c.Body.Contains(import, StringComparison.Ordinal)),
                    $"{language.Id}: expected import {import} in the imports fact.");
            }

            // Re-analysis of unchanged content must be candidate-identical, or the
            // pipeline's diff would supersede facts on every run.
            Assert.Equal(candidates, CodeAnalyzer.Analyze(filePath, language.Fixture.Source, language));
        }
    }

    [Fact]
    public void Extensions_AreClaimedByExactlyOneRow()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var language in LanguageRegistry.All)
        {
            Assert.NotEmpty(language.Extensions);
            foreach (var extension in language.Extensions)
            {
                Assert.False(
                    seen.TryGetValue(extension, out var owner),
                    $"{extension} claimed by both {owner} and {language.Id}; resolution would shadow one.");
                seen[extension] = language.Id;
            }
        }
    }

    [Fact]
    public void Resolve_MapsEveryClaimedExtensionToItsRow_AndEverythingElseToText()
    {
        foreach (var language in LanguageRegistry.All)
        {
            foreach (var extension in language.Extensions)
            {
                Assert.Same(language, LanguageRegistry.Resolve($"dir/file{extension}"));
            }
        }

        Assert.Same(LanguageRegistry.Text, LanguageRegistry.Resolve("Makefile"));
        Assert.Same(LanguageRegistry.Text, LanguageRegistry.Resolve("notes.xyz"));
    }

    /// <summary>
    /// D24's other direction: the analyzer must not know any language by name. A language
    /// id appearing in CodeAnalyzer.cs means a switch grew somewhere and the row stopped
    /// being the whole story.
    /// </summary>
    [Fact]
    public void CodeAnalyzer_CarriesNoLanguageIdLiterals()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "src", "Engram.Core", "CodeAnalyzer.cs"));

        foreach (var language in LanguageRegistry.All)
        {
            Assert.DoesNotContain($"\"{language.Id}\"", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "engram-schema.sql")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "could not locate the repository root from the test base directory");
        return directory.FullName;
    }
}
