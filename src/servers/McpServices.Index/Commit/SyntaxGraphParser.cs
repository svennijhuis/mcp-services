using System.Text;
using System.Text.RegularExpressions;
using McpServices.Index.Indexing;
using McpServices.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace McpServices.Index.Commit;

/// <summary>
/// Syntax graph used when no SCIP index is present. C# is parsed from the syntax tree only
/// (no compilation and no semantic model): declarations, calls, base types, and identifier
/// references. Other languages use the existing line patterns and get no edges. This is the
/// tree-sitter slot for this slice. It rebuilds the whole commit. It is not a proof.
/// </summary>
internal static partial class SyntaxGraphParser
{
    private const int MaxNameTargets = 8;

    public static void AddFile(CommitGraph graph, string path, string language, string text)
    {
        if (language == "csharp")
        {
            AddCSharp(graph, path, text);
            return;
        }

        var lines = Extractor.SplitLines(text);
        var extraction = RegexSymbolExtractor.Extract(language, lines);
        foreach (var symbol in extraction.Symbols)
        {
            var slice = Extractor.Join(lines, symbol.StartLine, symbol.EndLine);
            var key = SymbolKeys.Fallback(path, symbol.Kind, symbol.Name, symbol.StartLine, 1);
            graph.AddSymbol(new SymbolFact(key, null, path, symbol.Kind, symbol.Name, symbol.StartLine, 1, symbol.EndLine, 1, RepoIdentity.ContentHash(Encoding.UTF8.GetBytes(slice)), symbol.Doc, Trim(symbol.Signature, 400)));
        }
    }

    public static void Resolve(CommitGraph graph)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in graph.Symbols)
        {
            if (!byName.TryGetValue(symbol.Name, out var list))
            {
                list = [];
                byName[symbol.Name] = list;
            }

            list.Add(symbol.SymbolKey);
        }

        var seen = new HashSet<(string From, string To, string Kind)>();
        foreach (var pending in graph.Pending)
        {
            if (!byName.TryGetValue(pending.TargetName, out var targets) || targets.Count == 0 || targets.Count > MaxNameTargets)
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (target == pending.FromKey || !seen.Add((pending.FromKey, target, pending.Kind)))
                {
                    continue;
                }

                graph.Edges.Add(new EdgeFact(pending.FromKey, target, pending.Kind));
                graph.Occurrences.Add(new OccurrenceFact(target, pending.Path, pending.Line, pending.Col, pending.Line, pending.Col, OccurrenceRoles.Reference));
            }
        }

        graph.Pending.Clear();
    }

    private static void AddCSharp(CommitGraph graph, string path, string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse));
        var root = tree.GetCompilationUnitRoot();
        foreach (var member in root.Members)
        {
            Visit(graph, path, text, tree, member);
        }
    }

    private static void Visit(CommitGraph graph, string path, string text, SyntaxTree tree, MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax ns:
                foreach (var child in ns.Members)
                {
                    Visit(graph, path, text, tree, child);
                }

                return;
            case BaseTypeDeclarationSyntax type:
                {
                    var key = AddDeclaration(graph, path, text, tree, type, type.Identifier, KindOf(type));
                    if (type.BaseList is not null)
                    {
                        foreach (var baseType in type.BaseList.Types)
                        {
                            if (SimpleName(baseType.Type) is { } name)
                            {
                                var pos = Position(baseType.Type);
                                graph.Pending.Add(new PendingEdge(key, name, EdgeKinds.Implements, path, pos.Line, pos.Col));
                            }
                        }
                    }

                    if (type is TypeDeclarationSyntax declaration)
                    {
                        foreach (var child in declaration.Members)
                        {
                            Visit(graph, path, text, tree, child);
                        }
                    }
                    else if (type is EnumDeclarationSyntax enumDeclaration)
                    {
                        foreach (var enumMember in enumDeclaration.Members)
                        {
                            AddDeclaration(graph, path, text, tree, enumMember, enumMember.Identifier, "enummember");
                        }
                    }

                    return;
                }

            case MethodDeclarationSyntax method:
                CollectBody(graph, path, AddDeclaration(graph, path, text, tree, method, method.Identifier, "method"), method);
                return;
            case ConstructorDeclarationSyntax ctor:
                CollectBody(graph, path, AddDeclaration(graph, path, text, tree, ctor, ctor.Identifier, "constructor"), ctor);
                return;
            case PropertyDeclarationSyntax property:
                CollectBody(graph, path, AddDeclaration(graph, path, text, tree, property, property.Identifier, "property"), property);
                return;
            case IndexerDeclarationSyntax indexer:
                CollectBody(graph, path, AddDeclaration(graph, path, text, tree, indexer, indexer.ThisKeyword, "indexer"), indexer);
                return;
            case EventDeclarationSyntax ev:
                CollectBody(graph, path, AddDeclaration(graph, path, text, tree, ev, ev.Identifier, "event"), ev);
                return;
            case BaseFieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    AddDeclaration(graph, path, text, tree, variable, variable.Identifier, "field");
                }

                return;
            default:
                return;
        }
    }

    private static void CollectBody(CommitGraph graph, string path, string fromKey, SyntaxNode node)
    {
        var invocationNames = new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);
        foreach (var current in WithoutNestedTypes(node))
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation:
                    {
                        var nameNode = invocation.Expression switch
                        {
                            IdentifierNameSyntax id => (SyntaxNode)id,
                            MemberAccessExpressionSyntax access => access.Name,
                            MemberBindingExpressionSyntax binding => binding.Name,
                            _ => null,
                        };
                        if (nameNode is null || SimpleName(nameNode) is not { } name)
                        {
                            break;
                        }

                        invocationNames.Add(nameNode);
                        var pos = Position(nameNode);
                        graph.Pending.Add(new PendingEdge(fromKey, name, EdgeKinds.Calls, path, pos.Line, pos.Col));
                        break;
                    }

                case ObjectCreationExpressionSyntax creation when SimpleName(creation.Type) is { } created:
                    {
                        var pos = Position(creation.Type);
                        graph.Pending.Add(new PendingEdge(fromKey, created, EdgeKinds.References, path, pos.Line, pos.Col));
                        break;
                    }

                case IdentifierNameSyntax identifier when !invocationNames.Contains(identifier):
                    graph.Pending.Add(new PendingEdge(fromKey, identifier.Identifier.Text, EdgeKinds.References, path, Position(identifier).Line, Position(identifier).Col));
                    break;
            }
        }
    }

    private static string AddDeclaration(CommitGraph graph, string path, string text, SyntaxTree tree, SyntaxNode node, SyntaxToken identifier, string kind)
    {
        var (line, col) = TokenPosition(identifier);
        var (endLine, endCol) = EndPosition(tree, node);
        var slice = text.Substring(node.Span.Start, node.Span.Length);
        var key = SymbolKeys.Fallback(path, kind, identifier.Text, line, col);
        var signature = Trim(FirstLine(slice), 400);
        graph.AddSymbol(new SymbolFact(key, null, path, kind, identifier.Text, line, col, endLine, endCol, RepoIdentity.ContentHash(Encoding.UTF8.GetBytes(slice)), Doc(node), signature));
        return key;
    }

    private static IEnumerable<SyntaxNode> WithoutNestedTypes(SyntaxNode root)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in current.ChildNodes().Reverse())
            {
                if (child != root && child is BaseTypeDeclarationSyntax)
                {
                    continue;
                }

                stack.Push(child);
            }
        }
    }

    private static string KindOf(BaseTypeDeclarationSyntax type) => type switch
    {
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        RecordDeclarationSyntax => "record",
        _ => "type",
    };

    private static string? SimpleName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        QualifiedNameSyntax qualified => SimpleName(qualified.Right),
        AliasQualifiedNameSyntax alias => SimpleName(alias.Name),
        _ => null,
    };

    private static (int Line, int Col) Position(SyntaxNode node)
    {
        var pos = node.GetLocation().GetLineSpan().StartLinePosition;
        return (pos.Line + 1, pos.Character + 1);
    }

    private static (int Line, int Col) TokenPosition(SyntaxToken token)
    {
        var pos = token.GetLocation().GetLineSpan().StartLinePosition;
        return (pos.Line + 1, pos.Character + 1);
    }

    private static (int Line, int Col) EndPosition(SyntaxTree tree, SyntaxNode node)
    {
        if (node.Span.Length == 0)
        {
            return TokenPosition(node.GetFirstToken());
        }

        var last = tree.GetLineSpan(new TextSpan(node.Span.End - 1, 1)).StartLinePosition;
        return (last.Line + 1, last.Character + 1);
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
        var text = DocNoise().Replace(raw, " ");
        text = XmlTag().Replace(text, "$1");
        var compact = Whitespace().Replace(text, " ").Trim();
        return compact.Length == 0 ? null : Trim(compact, 2000);
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n');
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\s*///", RegexOptions.Multiline)]
    private static partial Regex DocNoise();

    [GeneratedRegex(@"<(?:see|paramref|typeparamref)\w*\s+\w+=""([^""]*)""\s*/>|<[^>]+>")]
    private static partial Regex XmlTag();
}
