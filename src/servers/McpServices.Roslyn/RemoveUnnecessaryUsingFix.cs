using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace McpServices.Roslyn;

/// <summary>
/// Fixes the compiler's hidden CS8019 ("unnecessary using directive"). Roslyn's own fixer is bound to the
/// IDE0005 analyzer, which only runs inside an IDE with editorconfig options, so headless callers get this one.
/// </summary>
public sealed class RemoveUnnecessaryUsingFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["CS8019"];

    public override FixAllProvider? GetFixAllProvider() => null;

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Remove unnecessary using",
                    cancellationToken => RemoveAsync(context.Document, diagnostic.Location.SourceSpan, cancellationToken),
                    "McpServices.Roslyn.RemoveUnnecessaryUsing"),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    private static async Task<Document> RemoveAsync(Document document, Microsoft.CodeAnalysis.Text.TextSpan span, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var directive = root.FindNode(span).AncestorsAndSelf().OfType<UsingDirectiveSyntax>().FirstOrDefault();
        if (directive is null)
        {
            return document;
        }

        // Leading trivia (file headers) stays with the next node; the directive's own line break goes.
        var newRoot = root.RemoveNode(directive, SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives);
        return newRoot is null ? document : document.WithSyntaxRoot(newRoot);
    }
}
