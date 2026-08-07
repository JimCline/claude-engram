using Engram.Core;

namespace Engram.Core.Tests;

public class CodeAnalyzerTests
{
    private const string FilePath = "/projects/p/code/r/src/Sample.cs";

    private static LanguageDefinition CSharp() =>
        LanguageRegistry.All.Single(language => language.Id == "csharp");

    private static LanguageDefinition Markdown() =>
        LanguageRegistry.All.Single(language => language.DocHeadings);

    [Fact]
    public void CSharp_NestedTypes_AreLeftToTheDeepTier()
    {
        var source = """
            namespace Demo;

            public class Outer
            {
                    public class Inner { }
            }
            """;

        var candidates = CodeAnalyzer.Analyze(FilePath, source, CSharp());

        Assert.Contains(candidates, c => c.Kind == "symbol" && c.DisplayName == "Outer");
        // Grammar v1 defines no nested-symbol paths; writing Inner here would mint an
        // address the deep tier then has to adopt away (D2).
        Assert.DoesNotContain(candidates, c => c.DisplayName == "Inner");
    }

    [Fact]
    public void CSharp_DeclarationFact_CarriesTheDeclarationLineVerbatim()
    {
        var source = "public sealed record FactWrite(string SubjectPath);\n";

        var candidates = CodeAnalyzer.Analyze(FilePath, source, CSharp());

        var declaration = Assert.Single(candidates, c => c.Predicate == "declared-as");
        Assert.Equal("/projects/p/code/r/src/Sample.cs#FactWrite", declaration.EntityPath);
        Assert.Equal("public sealed record FactWrite(string SubjectPath);", declaration.Body);
    }

    [Fact]
    public void CSharp_Imports_AreOneSortedFact_SoReorderingIsNotAChange()
    {
        var forward = "using Zebra.Lib;\nusing Alpha.Lib;\nclass C { }";
        var reversed = "using Alpha.Lib;\nusing Zebra.Lib;\nclass C { }";

        var first = CodeAnalyzer.Analyze(FilePath, forward, CSharp()).Single(c => c.Predicate == "imports");
        var second = CodeAnalyzer.Analyze(FilePath, reversed, CSharp()).Single(c => c.Predicate == "imports");

        Assert.Equal(first.Body, second.Body);
        Assert.Equal("imports Alpha.Lib, Zebra.Lib", first.Body);
    }

    [Fact]
    public void Markdown_PreambleBecomesTheFileImpression_AndSectionsNest()
    {
        var source = """
            Widgets turn cranks into torque, and this manual covers all of them.

            # Manual

            ## Install

            Run the installer with defaults everywhere.

            ### Prerequisites

            A crank, and somewhere to put it.
            """;

        var candidates = CodeAnalyzer.Analyze("/projects/p/code/r/README.md", source, Markdown());

        var about = Assert.Single(candidates, c => c.Kind == "file");
        Assert.StartsWith("Widgets turn cranks", about.Body, StringComparison.Ordinal);

        Assert.Contains(candidates, c =>
            c.EntityPath == "/projects/p/code/r/README.md#manual/install"
            && c.Body.Contains("installer", StringComparison.Ordinal));
        Assert.Contains(candidates, c =>
            c.EntityPath == "/projects/p/code/r/README.md#manual/install/prerequisites");
    }

    [Fact]
    public void Markdown_SectionDisplayName_IsTheHeadingAsAuthored()
    {
        var source = "# Claude Code Hooks\n\nHooks fire around tool calls.\n";

        var candidates = CodeAnalyzer.Analyze("/p/README.md", source, Markdown());

        var section = Assert.Single(candidates, c => c.Kind == "section");
        Assert.Equal("Claude Code Hooks", section.DisplayName);
        Assert.EndsWith("#claude-code-hooks", section.EntityPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TextFallback_GetsAnImpressionAndNothingElse()
    {
        var candidates = CodeAnalyzer.Analyze(
            "/projects/p/code/r/notes.txt",
            "Deployment notes for the staging box. Rotate the key quarterly.",
            LanguageRegistry.Text);

        var about = Assert.Single(candidates);
        Assert.Equal("about", about.Predicate);
        Assert.Contains("staging", about.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionSlugsColliding_MergeIntoOneSection()
    {
        var source = """
            # Setup

            First half.

            # SETUP!

            Second half.
            """;

        var candidates = CodeAnalyzer.Analyze("/p/doc.md", source, Markdown());

        var sections = candidates.Where(c => c.Kind == "section").ToList();
        var section = Assert.Single(sections);
        Assert.EndsWith("#setup", section.EntityPath, StringComparison.Ordinal);
        Assert.Contains("First half", section.Body, StringComparison.Ordinal);
    }
}
