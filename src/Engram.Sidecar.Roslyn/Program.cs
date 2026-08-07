using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// The tier-2 C# analyzer (D24), spoken to over stdin/stdout and never over the database:
// the sidecar has no home, no store, and no configuration — it is a pure function from
// source text to structure, which is what keeps it safe to version separately from the
// core and trivial to kill on timeout.
//
// Protocol, one JSON object per line both ways:
//   in:  {"path": "<repo-relative>", "content": "<source>"}
//   out: {"path": ..., "symbols": [{"name","kind","declaration","doc"}], "imports": [...]}
//   out on a file it cannot analyze: {"path": ..., "error": "..."} — the core falls back
//   to tier 0 for that file and nothing else.
//
// Syntax only, deliberately: a semantic model needs references and produces overload
// signatures, which grammar v1 cannot address anyway (D27). Symbols are top-level type
// declarations, matching the paths tier 0 already writes, so a store indexed by either
// tier re-keys nothing when the other takes over.

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonObject? request;
    try
    {
        request = JsonNode.Parse(line) as JsonObject;
    }
    catch (System.Text.Json.JsonException)
    {
        request = null;
    }

    var path = request?["path"]?.GetValue<string>();
    if (request is null || path is null)
    {
        Console.WriteLine(new JsonObject { ["error"] = "unparseable request line" }.ToJsonString());
        continue;
    }

    try
    {
        Console.WriteLine(Analyze(path, request["content"]?.GetValue<string>() ?? string.Empty).ToJsonString());
    }
    catch (Exception exception)
    {
        Console.WriteLine(new JsonObject
        {
            ["path"] = path,
            ["error"] = exception.Message,
        }.ToJsonString());
    }
}

static JsonObject Analyze(string path, string content)
{
    var root = CSharpSyntaxTree.ParseText(content).GetRoot();

    var symbols = new JsonArray();
    foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
    {
        // Top-level only: grammar v1 has no fragment for a nested type, and emitting one
        // would write paths recall can never be asked for by that grammar.
        if (declaration.Parent is BaseTypeDeclarationSyntax)
        {
            continue;
        }

        ((IList<JsonNode?>)symbols).Add(new JsonObject
        {
            ["name"] = declaration.Identifier.Text,
            ["kind"] = declaration.Kind() switch
            {
                SyntaxKind.ClassDeclaration => "class",
                SyntaxKind.InterfaceDeclaration => "interface",
                SyntaxKind.StructDeclaration => "struct",
                SyntaxKind.RecordDeclaration => "record",
                SyntaxKind.RecordStructDeclaration => "record struct",
                SyntaxKind.EnumDeclaration => "enum",
                _ => "type",
            },
            ["declaration"] = DeclarationLine(declaration),
            ["doc"] = DocSummary(declaration),
        });
    }

    foreach (var declaration in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
    {
        if (declaration.Parent is BaseTypeDeclarationSyntax)
        {
            continue;
        }

        ((IList<JsonNode?>)symbols).Add(new JsonObject
        {
            ["name"] = declaration.Identifier.Text,
            ["kind"] = "delegate",
            ["declaration"] = DeclarationLine(declaration),
            ["doc"] = DocSummary(declaration),
        });
    }

    var imports = new JsonArray();
    foreach (var name in root.DescendantNodes().OfType<UsingDirectiveSyntax>()
        .Where(u => u.Name is not null)
        .Select(u => u.Name!.ToString())
        .Distinct()
        .OrderBy(n => n, StringComparer.Ordinal))
    {
        ((IList<JsonNode?>)imports).Add(JsonValue.Create(name));
    }

    return new JsonObject
    {
        ["path"] = path,
        ["symbols"] = symbols,
        ["imports"] = imports,
    };
}

static string DeclarationLine(MemberDeclarationSyntax declaration)
{
    // The declaration as written, up to the body: modifiers, keyword, name, type
    // parameters, bases — one line, whatever the source's own formatting was.
    var text = declaration switch
    {
        BaseTypeDeclarationSyntax type => type.ToString()[..LengthBeforeBody(type)],
        _ => declaration.ToString(),
    };

    var flattened = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return flattened.Length > 200 ? flattened[..200] : flattened;
}

static int LengthBeforeBody(BaseTypeDeclarationSyntax type)
{
    if (type.OpenBraceToken.IsKind(SyntaxKind.None))
    {
        return type.ToString().TrimEnd(';', ' ', '\n', '\r').Length;
    }

    return Math.Max(1, type.OpenBraceToken.SpanStart - type.SpanStart);
}

static string? DocSummary(MemberDeclarationSyntax declaration)
{
    var doc = declaration.GetLeadingTrivia()
        .Select(t => t.GetStructure())
        .OfType<DocumentationCommentTriviaSyntax>()
        .FirstOrDefault();

    var summary = doc?.DescendantNodes().OfType<XmlElementSyntax>()
        .FirstOrDefault(e => e.StartTag.Name.ToString() == "summary");
    if (summary is null)
    {
        return null;
    }

    var text = string.Join(
        ' ',
        summary.Content.ToString()
            .Replace("///", " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Doc-comment tags like <see cref="..."/> read as markup, not prose.
    text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", string.Empty);
    text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    return text.Length == 0 ? null : text.Length > 300 ? text[..300] : text;
}
