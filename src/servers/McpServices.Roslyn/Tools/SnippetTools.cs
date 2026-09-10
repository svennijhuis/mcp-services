using System.ComponentModel;
using McpServices.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace McpServices.Roslyn.Tools;

[McpServerToolType]
public sealed class SnippetTools(WorkspaceManager workspaces)
{
    [McpServerTool(Name = "analyze_snippet", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Analyze snippet")]
    [Description("Compile a standalone C# snippet against the .NET runtime (no workspace needed) and return diagnostics, declared symbols and, optionally, the syntax tree. Use for quick 'does this compile?' checks and explaining code.")]
    public async Task<object> AnalyzeSnippet(
        [Description("C# source code (a full file or top-level statements).")] string code,
        [Description("Include the declared types/members (default true).")] bool includeSymbols = true,
        [Description("Include a syntax tree outline (default false).")] bool includeSyntaxTree = false,
        [Description("Minimum severity to report: hidden | info | warning | error (default warning).")] string? minSeverity = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(code, "code");
        var minimum = DiagnosticsTools.ParseSeverity(minSeverity ?? "warning");
        using var snippet = new SnippetScope(code);
        var document = snippet.Document;
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) ?? throw new ToolException("No semantic model.");
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) ?? throw new ToolException("No syntax root.");
        var solution = document.Project.Solution;

        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Severity >= minimum)
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .Take(200)
            .Select(d => DiagnosticsTools.Describe(d, solution))
            .ToList();

        List<object>? symbols = null;
        if (includeSymbols)
        {
            symbols = [];
            foreach (var node in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                IEnumerable<ISymbol?> declared = node switch
                {
                    FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, cancellationToken)),
                    GlobalStatementSyntax or BaseNamespaceDeclarationSyntax or IncompleteMemberSyntax => [],
                    _ => [model.GetDeclaredSymbol(node, cancellationToken)],
                };
                foreach (var symbol in declared.OfType<ISymbol>())
                {
                    var span = node.GetLocation().GetLineSpan();
                    symbols.Add(new { symbol.Name, kind = Symbols.Kind(symbol), signature = symbol.ToDisplayString(Symbols.SignatureFormat), line = span.StartLinePosition.Line + 1, endLine = span.EndLinePosition.Line + 1 });
                }
            }
        }

        var errors = model.GetDiagnostics(cancellationToken: cancellationToken).Count(d => d.Severity == DiagnosticSeverity.Error);
        return new
        {
            compiles = errors == 0,
            errors,
            diagnostics,
            symbols,
            usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.NamespaceOrType?.ToString()).ToList(),
            hasTopLevelStatements = root.DescendantNodes().OfType<GlobalStatementSyntax>().Any(),
            syntaxTree = includeSyntaxTree ? Outline(root, 0, 6) : null,
        };
    }

    [McpServerTool(Name = "get_syntax_tree", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Syntax tree")]
    [Description("Syntax tree outline (node kinds with line ranges) of a workspace file or of inline code, limited to a maximum depth. Handy for understanding how Roslyn sees a construct.")]
    public async Task<object> GetSyntaxTree(
        [Description("File path in a loaded workspace.")] string? file = null,
        [Description("Inline C# code instead of a file.")] string? code = null,
        [Description("Only the subtree covering this 1-based line.")] int? line = null,
        [Description("Maximum depth (default 4, max 12).")] int? maxDepth = null,
        [Description("Include tokens as leaves (default false).")] bool includeTokens = false,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        SyntaxNode root;
        string? path;
        if (!string.IsNullOrEmpty(code))
        {
            root = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: cancellationToken).GetRoot(cancellationToken);
            path = null;
        }
        else if (!string.IsNullOrEmpty(file))
        {
            var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            var document = Symbols.GetDocument(session.Solution, file);
            root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) ?? throw new ToolException("No syntax root.");
            path = document.FilePath;
        }
        else
        {
            throw new ToolException("Pass 'file' or 'code'.");
        }

        if (line is { } l)
        {
            var text = root.SyntaxTree.GetText(cancellationToken);
            if (l < 1 || l > text.Lines.Count)
            {
                throw new ToolException($"Line {l} is out of range (1-{text.Lines.Count}).");
            }

            var lineSpan = text.Lines[l - 1].Span;
            root = root.DescendantNodesAndSelf().Where(n => n.Span.Contains(lineSpan)).OrderBy(n => n.Span.Length).FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax) ?? root.FindNode(lineSpan);
        }

        return new { file = path, root = Outline(root, 0, Math.Clamp(maxDepth ?? 4, 1, 12), includeTokens) };
    }

    internal static object Outline(SyntaxNode node, int depth, int maxDepth, bool includeTokens = false)
    {
        var span = node.GetLocation().GetLineSpan();
        object? children = null;
        if (depth < maxDepth)
        {
            var list = new List<object>();
            foreach (var child in node.ChildNodesAndTokens())
            {
                if (child.AsNode() is { } childNode)
                {
                    list.Add(Outline(childNode, depth + 1, maxDepth, includeTokens));
                }
                else if (includeTokens && child.AsToken() is { } token && !token.IsKind(SyntaxKind.None))
                {
                    list.Add(new { kind = token.Kind().ToString(), text = token.Text });
                }
            }

            children = list.Count == 0 ? null : list;
        }

        var text = node.ToString();
        return new
        {
            kind = node.Kind().ToString(),
            line = span.StartLinePosition.Line + 1,
            endLine = span.EndLinePosition.Line + 1,
            text = depth >= maxDepth || node.ChildNodes().Any() is false ? (text.Length > 80 ? text[..80] + "…" : text) : null,
            children,
        };
    }

    /// <summary>An ad-hoc workspace that lives for one call.</summary>
    internal sealed class SnippetScope : IDisposable
    {
        private readonly AdhocWorkspace _workspace;

        public SnippetScope(string code, string? fileName = null)
        {
            (_workspace, Document) = WorkspaceManager.CreateSnippet(code, fileName);
        }

        public Document Document { get; }

        public void Dispose() => _workspace.Dispose();
    }
}
