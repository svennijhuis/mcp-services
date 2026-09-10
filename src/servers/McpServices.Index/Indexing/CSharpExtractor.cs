using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace McpServices.Index.Indexing;

/// <summary>
/// Syntax-only C# extraction (no MSBuild, no semantic model): types and members with signatures
/// and XML doc summaries, plus one chunk per member so search hits land on the right method.
/// </summary>
public static partial class CSharpExtractor
{
    public static Extraction Extract(string text, string[] lines)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse));
        var root = tree.GetCompilationUnitRoot();
        var symbols = new List<ExtractedSymbol>();
        var chunks = new List<ExtractedChunk>();

        foreach (var member in root.Members)
        {
            Visit(member, null, null, symbols, chunks, lines);
        }

        if (chunks.Count == 0)
        {
            chunks = Extractor.WindowChunks(lines, 0);
        }

        return new Extraction(symbols, chunks, lines.Length);
    }

    private static void Visit(MemberDeclarationSyntax member, string? ns, string? container, List<ExtractedSymbol> symbols, List<ExtractedChunk> chunks, string[] lines)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                var name = namespaceDeclaration.Name.ToString();
                var fullNs = ns is null ? name : ns + "." + name;
                foreach (var child in namespaceDeclaration.Members)
                {
                    Visit(child, fullNs, null, symbols, chunks, lines);
                }

                return;

            case BaseTypeDeclarationSyntax type:
                {
                    var typeName = type.Identifier.Text + Arity(type);
                    var fullName = Qualify(ns, container, typeName);
                    var (start, end) = Lines(type);
                    symbols.Add(new ExtractedSymbol(type.Identifier.Text, fullName, Kind(type), container ?? ns, Signature(type), Doc(type), start, end));

                    if (type is EnumDeclarationSyntax enumDeclaration)
                    {
                        foreach (var enumMember in enumDeclaration.Members)
                        {
                            var (s, e) = Lines(enumMember);
                            symbols.Add(new ExtractedSymbol(enumMember.Identifier.Text, fullName + "." + enumMember.Identifier.Text, "enummember", fullName, enumMember.ToString(), Doc(enumMember), s, e));
                        }

                        AddChunk(chunks, lines, start, end, fullName);
                        return;
                    }

                    if (type is TypeDeclarationSyntax typeDeclaration)
                    {
                        // Header chunk: attributes, declaration line, primary constructor, base list.
                        var firstMemberLine = typeDeclaration.Members.Count > 0 ? Lines(typeDeclaration.Members[0]).Start : end;
                        AddChunk(chunks, lines, start, Math.Max(start, firstMemberLine - 1), fullName);
                        foreach (var child in typeDeclaration.Members)
                        {
                            Visit(child, ns, fullName, symbols, chunks, lines);
                        }
                    }

                    return;
                }

            case DelegateDeclarationSyntax @delegate:
                {
                    var (start, end) = Lines(@delegate);
                    var fullName = Qualify(ns, container, @delegate.Identifier.Text);
                    symbols.Add(new ExtractedSymbol(@delegate.Identifier.Text, fullName, "delegate", container ?? ns, Header(@delegate), Doc(@delegate), start, end));
                    AddChunk(chunks, lines, start, end, fullName);
                    return;
                }

            case MethodDeclarationSyntax method:
                AddMember(method, method.Identifier.Text + Arity(method.TypeParameterList), "method", ns, container, symbols, chunks, lines);
                return;
            case ConstructorDeclarationSyntax ctor:
                AddMember(ctor, ctor.Identifier.Text, "constructor", ns, container, symbols, chunks, lines);
                return;
            case DestructorDeclarationSyntax dtor:
                AddMember(dtor, "~" + dtor.Identifier.Text, "destructor", ns, container, symbols, chunks, lines);
                return;
            case PropertyDeclarationSyntax property:
                AddMember(property, property.Identifier.Text, "property", ns, container, symbols, chunks, lines);
                return;
            case IndexerDeclarationSyntax indexer:
                AddMember(indexer, "this[]", "indexer", ns, container, symbols, chunks, lines);
                return;
            case EventDeclarationSyntax @event:
                AddMember(@event, @event.Identifier.Text, "event", ns, container, symbols, chunks, lines);
                return;
            case OperatorDeclarationSyntax op:
                AddMember(op, "operator " + op.OperatorToken.Text, "operator", ns, container, symbols, chunks, lines);
                return;
            case ConversionOperatorDeclarationSyntax conversion:
                AddMember(conversion, "operator " + conversion.Type, "operator", ns, container, symbols, chunks, lines);
                return;
            case BaseFieldDeclarationSyntax field:
                {
                    var (start, end) = Lines(field);
                    var kind = field is EventFieldDeclarationSyntax ? "event" : field.Modifiers.Any(SyntaxKind.ConstKeyword) ? "constant" : "field";
                    foreach (var variable in field.Declaration.Variables)
                    {
                        symbols.Add(new ExtractedSymbol(variable.Identifier.Text, Qualify(ns, container, variable.Identifier.Text), kind, container, Header(field), Doc(field), start, end));
                    }

                    AddChunk(chunks, lines, start, end, container);
                    return;
                }

            case GlobalStatementSyntax global:
                {
                    var (start, end) = Lines(global);
                    AddChunk(chunks, lines, start, end, "top-level statements");
                    return;
                }
        }
    }

    private static void AddMember(MemberDeclarationSyntax member, string name, string kind, string? ns, string? container, List<ExtractedSymbol> symbols, List<ExtractedChunk> chunks, string[] lines)
    {
        var (start, end) = Lines(member);
        var fullName = Qualify(ns, container, name);
        symbols.Add(new ExtractedSymbol(name, fullName, kind, container, Header(member), Doc(member), start, end));
        AddChunk(chunks, lines, start, end, fullName);
    }

    private static void AddChunk(List<ExtractedChunk> chunks, string[] lines, int start, int end, string? heading)
    {
        if (end < start)
        {
            return;
        }

        if (end - start + 1 <= Extractor.MaxChunkLines)
        {
            var text = Extractor.Join(lines, start, end);
            if (text.Trim().Length > 0)
            {
                chunks.Add(new ExtractedChunk(chunks.Count, start, end, heading, Extractor.Truncate(text)));
            }

            return;
        }

        chunks.AddRange(Extractor.WindowChunks(lines, chunks.Count, start, end, heading));
    }

    private static (int Start, int End) Lines(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        // Start at the declaration itself (skip leading comments/blank lines), which is what agents want to jump to.
        var declarationStart = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
        return (declarationStart, span.EndLinePosition.Line + 1);
    }

    private static string Qualify(string? ns, string? container, string name) =>
        container is not null ? container + "." + name : ns is not null ? ns + "." + name : name;

    private static string Kind(BaseTypeDeclarationSyntax type) => type switch
    {
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
        _ => "type",
    };

    private static string Arity(BaseTypeDeclarationSyntax type) =>
        type is TypeDeclarationSyntax { TypeParameterList: { } list } ? Arity(list) : string.Empty;

    private static string Arity(TypeParameterListSyntax? list) =>
        list is null || list.Parameters.Count == 0 ? string.Empty : "<" + string.Join(", ", list.Parameters.Select(p => p.Identifier.Text)) + ">";

    private static string Signature(BaseTypeDeclarationSyntax type)
    {
        var sb = new StringBuilder();
        sb.Append(type.Modifiers.ToString()).Append(' ');
        sb.Append(type switch
        {
            RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
            TypeDeclarationSyntax t => t.Keyword.Text,
            EnumDeclarationSyntax => "enum",
            _ => "type",
        });
        sb.Append(' ').Append(type.Identifier.Text).Append(Arity(type));
        if (type is TypeDeclarationSyntax { ParameterList: { } parameters })
        {
            sb.Append(Compact(parameters.ToString()));
        }

        if (type.BaseList is not null)
        {
            sb.Append(' ').Append(Compact(type.BaseList.ToString()));
        }

        return Compact(sb.ToString());
    }

    /// <summary>Declaration without body, attributes or leading trivia, on one line.</summary>
    private static string Header(MemberDeclarationSyntax member)
    {
        SyntaxNode node = member switch
        {
            MethodDeclarationSyntax m => m.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default),
            ConstructorDeclarationSyntax c => c.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default),
            DestructorDeclarationSyntax d => d.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default),
            OperatorDeclarationSyntax o => o.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default),
            ConversionOperatorDeclarationSyntax o => o.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default),
            PropertyDeclarationSyntax p => p.WithAccessorList(null).WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(default),
            IndexerDeclarationSyntax i => i.WithAccessorList(null).WithExpressionBody(null).WithSemicolonToken(default),
            EventDeclarationSyntax e => e.WithAccessorList(null),
            BaseFieldDeclarationSyntax f => f.WithDeclaration(f.Declaration.WithVariables(
                SyntaxFactory.SeparatedList(f.Declaration.Variables.Select(v => v.WithInitializer(null))))),
            _ => member,
        };

        var withoutAttributes = node is MemberDeclarationSyntax m2 ? m2.WithAttributeLists(default) : node;
        var text = withoutAttributes.WithoutTrivia().ToString();
        if (member is PropertyDeclarationSyntax property && property.AccessorList is not null)
        {
            text += " { " + string.Join(" ", property.AccessorList.Accessors.Select(a => (a.Modifiers.Count > 0 ? a.Modifiers.ToString() + " " : string.Empty) + a.Keyword.Text + ";")) + " }";
        }

        return Compact(text);
    }

    private static string? Doc(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia().Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().FirstOrDefault();
        if (trivia is null)
        {
            return null;
        }

        var summary = trivia.Content.OfType<XmlElementSyntax>().FirstOrDefault(e => e.StartTag.Name.LocalName.Text == "summary");
        var raw = summary is not null ? summary.Content.ToString() : trivia.Content.ToString();
        var text = DocNoiseRegex().Replace(raw, " ");
        text = XmlTagRegex().Replace(text, "$1");
        return Compact(text) is { Length: > 0 } compact ? compact : null;
    }

    private static string Compact(string text) =>
        WhitespaceRegex().Replace(text, " ").Replace(" ;", ";", StringComparison.Ordinal).Replace(" ,", ",", StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\s*///", RegexOptions.Multiline)]
    private static partial Regex DocNoiseRegex();

    [GeneratedRegex(@"<(?:see|paramref|typeparamref)\w*\s+\w+=""([^""]*)""\s*/>|<[^>]+>")]
    private static partial Regex XmlTagRegex();
}
