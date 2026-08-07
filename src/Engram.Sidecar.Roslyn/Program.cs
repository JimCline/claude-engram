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
//   out: {"path": ..., "symbols": [{"name","kind","declaration","doc","scope"?,"params"?}],
//         "imports": [...]}
//   out on a file it cannot analyze: {"path": ..., "error": "..."} — the core falls back
//   to tier 0 for that file and nothing else.
//
// Syntax only, deliberately: a semantic model needs references, and grammar v2's addresses
// are syntactic on purpose (D48) — scope and params ship as written, raw; the core's
// DeepTier composes every fragment, so this process never spells an address. Types are
// emitted at any depth. Members are emitted when they are surface: an explicit public,
// internal, or protected modifier, or membership in an interface — a bare private member
// is implementation, the same line the registry draws for unexported bindings. Enum
// members, indexers, and operators are deliberately not emitted (D48).

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
        var scope = ScopeOf(declaration);
        Emit(symbols, declaration.Identifier.Text, KindOf(declaration), declaration, scope, parameters: null);

        // Enums are BaseType but not Type: their members are values, not surface (D48).
        if (declaration is TypeDeclarationSyntax type)
        {
            var memberScope = scope is null
                ? declaration.Identifier.Text
                : scope + "/" + declaration.Identifier.Text;
            var inInterface = declaration is InterfaceDeclarationSyntax;
            foreach (var member in type.Members)
            {
                EmitMember(symbols, member, memberScope, inInterface);
            }
        }
    }

    foreach (var declaration in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
    {
        Emit(
            symbols,
            declaration.Identifier.Text,
            "delegate",
            declaration,
            ScopeOf(declaration),
            declaration.ParameterList.ToString());
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

static void EmitMember(JsonArray symbols, MemberDeclarationSyntax member, string scope, bool inInterface)
{
    if (!inInterface && !member.Modifiers.Any(m =>
        m.IsKind(SyntaxKind.PublicKeyword)
            || m.IsKind(SyntaxKind.InternalKeyword)
            || m.IsKind(SyntaxKind.ProtectedKeyword)))
    {
        return;
    }

    switch (member)
    {
        case MethodDeclarationSyntax method:
            Emit(symbols, method.Identifier.Text, "method", method, scope, method.ParameterList.ToString());
            break;
        case ConstructorDeclarationSyntax constructor:
            Emit(symbols, constructor.Identifier.Text, "constructor", constructor, scope, constructor.ParameterList.ToString());
            break;
        case PropertyDeclarationSyntax property:
            Emit(symbols, property.Identifier.Text, "property", property, scope, parameters: null);
            break;
        case FieldDeclarationSyntax field:
            foreach (var declarator in field.Declaration.Variables)
            {
                Emit(symbols, declarator.Identifier.Text, "field", field, scope, parameters: null);
            }

            break;
        case EventFieldDeclarationSyntax eventField:
            foreach (var declarator in eventField.Declaration.Variables)
            {
                Emit(symbols, declarator.Identifier.Text, "event", eventField, scope, parameters: null);
            }

            break;
        case EventDeclarationSyntax eventDeclaration:
            Emit(symbols, eventDeclaration.Identifier.Text, "event", eventDeclaration, scope, parameters: null);
            break;
    }
}

static void Emit(
    JsonArray symbols,
    string name,
    string kind,
    MemberDeclarationSyntax declaration,
    string? scope,
    string? parameters)
{
    var symbol = new JsonObject
    {
        ["name"] = name,
        ["kind"] = kind,
        ["declaration"] = DeclarationLine(declaration),
        ["doc"] = DocSummary(declaration),
    };

    if (scope is not null)
    {
        symbol["scope"] = scope;
    }

    if (parameters is not null)
    {
        symbol["params"] = parameters;
    }

    ((IList<JsonNode?>)symbols).Add(symbol);
}

static string? ScopeOf(SyntaxNode declaration)
{
    List<string>? names = null;
    for (var parent = declaration.Parent; parent is not null; parent = parent.Parent)
    {
        if (parent is BaseTypeDeclarationSyntax type)
        {
            (names ??= []).Add(type.Identifier.Text);
        }
    }

    if (names is null)
    {
        return null;
    }

    names.Reverse();
    return string.Join('/', names);
}

static string KindOf(BaseTypeDeclarationSyntax declaration) => declaration.Kind() switch
{
    SyntaxKind.ClassDeclaration => "class",
    SyntaxKind.InterfaceDeclaration => "interface",
    SyntaxKind.StructDeclaration => "struct",
    SyntaxKind.RecordDeclaration => "record",
    SyntaxKind.RecordStructDeclaration => "record struct",
    SyntaxKind.EnumDeclaration => "enum",
    _ => "type",
};

static string DeclarationLine(MemberDeclarationSyntax declaration)
{
    // The declaration as written, up to the body: a declared-as fact carries a signature,
    // not an implementation. Auto-accessor shapes ({ get; set; }) stay — they are contract.
    var text = declaration switch
    {
        BaseTypeDeclarationSyntax type => type.ToString()[..LengthBeforeBody(type)],
        MethodDeclarationSyntax method => Signature(method, method.Body, method.ExpressionBody),
        ConstructorDeclarationSyntax constructor => Signature(constructor, constructor.Body, constructor.ExpressionBody),
        PropertyDeclarationSyntax { ExpressionBody: not null } property => Cut(property, property.ExpressionBody!.SpanStart),
        PropertyDeclarationSyntax property when HasComputedAccessor(property) => Cut(property, property.AccessorList!.SpanStart),
        _ => declaration.ToString(),
    };

    var flattened = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return flattened.Length > 200 ? flattened[..200] : flattened;
}

static string Signature(MemberDeclarationSyntax member, SyntaxNode? body, SyntaxNode? expressionBody) =>
    (body?.SpanStart ?? expressionBody?.SpanStart) is { } position
        ? Cut(member, position)
        : member.ToString().TrimEnd(';', ' ', '\n', '\r');

static string Cut(MemberDeclarationSyntax member, int position) =>
    member.ToString()[..Math.Max(1, position - member.SpanStart)];

static bool HasComputedAccessor(PropertyDeclarationSyntax property) =>
    property.AccessorList?.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null) == true;

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
