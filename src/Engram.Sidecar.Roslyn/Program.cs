using System.Runtime.CompilerServices;
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
    var attribution = new List<SyntaxNode>();
    foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
    {
        var scope = ScopeOf(declaration);
        Emit(symbols, attribution, declaration.Identifier.Text, KindOf(declaration), declaration, scope, parameters: null);

        // Enums are BaseType but not Type: their members are values, not surface (D48).
        if (declaration is TypeDeclarationSyntax type)
        {
            var memberScope = scope is null
                ? declaration.Identifier.Text
                : scope + "/" + declaration.Identifier.Text;
            var inInterface = declaration is InterfaceDeclarationSyntax;
            foreach (var member in type.Members)
            {
                EmitMember(symbols, attribution, member, memberScope, inInterface);
            }
        }
    }

    foreach (var declaration in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
    {
        Emit(
            symbols,
            attribution,
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
        ["calls"] = CallsOf(root, attribution),
    };
}

static void EmitMember(
    JsonArray symbols, List<SyntaxNode> attribution, MemberDeclarationSyntax member, string scope, bool inInterface)
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
            Emit(symbols, attribution, method.Identifier.Text, "method", method, scope, method.ParameterList.ToString());
            break;
        case ConstructorDeclarationSyntax constructor:
            Emit(symbols, attribution, constructor.Identifier.Text, "constructor", constructor, scope, constructor.ParameterList.ToString());
            break;
        case PropertyDeclarationSyntax property:
            Emit(symbols, attribution, property.Identifier.Text, "property", property, scope, parameters: null);
            break;
        case FieldDeclarationSyntax field:
            foreach (var declarator in field.Declaration.Variables)
            {
                // Each co-declared field gets its own attribution point (the declarator, not
                // the shared field node) — two fields on one line must not share one address.
                Emit(symbols, attribution, declarator.Identifier.Text, "field", field, scope, parameters: null, declarator);
            }

            break;
        case EventFieldDeclarationSyntax eventField:
            foreach (var declarator in eventField.Declaration.Variables)
            {
                Emit(symbols, attribution, declarator.Identifier.Text, "event", eventField, scope, parameters: null, declarator);
            }

            break;
        case EventDeclarationSyntax eventDeclaration:
            Emit(symbols, attribution, eventDeclaration.Identifier.Text, "event", eventDeclaration, scope, parameters: null);
            break;
    }
}

static void Emit(
    JsonArray symbols,
    List<SyntaxNode> attribution,
    string name,
    string kind,
    MemberDeclarationSyntax declaration,
    string? scope,
    string? parameters,
    SyntaxNode? attributionNode = null)
{
    var declarationLine = DeclarationLine(declaration);
    var doc = DocSummary(declaration);
    var id = attribution.Count;

    var symbol = new JsonObject
    {
        ["id"] = id,
        ["name"] = name,
        ["kind"] = kind,
        ["declaration"] = declarationLine,
        ["doc"] = doc,
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
    attribution.Add(attributionNode ?? declaration);
}

/// <summary>
/// One JSON call entry per <see cref="InvocationExpressionSyntax"/>: the callee as written,
/// its 1-based line, and the `id` of the nearest emitted symbol enclosing it. Emission is
/// the public surface (`EmitMember` skips non-public members), so a call inside a private
/// method or a local function attributes to the nearest emitted ancestor — usually the
/// enclosing type — never to nothing. A walk that reaches the root leaves `enclosing_id`
/// absent, which core attributes to the file (§5.2.1 of the Phase 3 spec). This emits an
/// id, not a fragment: assembling the address is core's job (§5.3.1).
/// </summary>
static JsonArray CallsOf(SyntaxNode root, List<SyntaxNode> attribution)
{
    var idOf = new Dictionary<SyntaxNode, int>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < attribution.Count; i++)
    {
        idOf[attribution[i]] = i;
    }

    var calls = new JsonArray();
    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        int? enclosingId = null;
        for (var ancestor = invocation.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (idOf.TryGetValue(ancestor, out var id))
            {
                enclosingId = id;
                break;
            }
        }

        var call = new JsonObject
        {
            ["callee"] = invocation.Expression.ToString(),
            ["line"] = invocation.Expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
        };

        if (enclosingId is not null)
        {
            call["enclosing_id"] = enclosingId;
        }

        ((IList<JsonNode?>)calls).Add(call);
    }

    return calls;
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
