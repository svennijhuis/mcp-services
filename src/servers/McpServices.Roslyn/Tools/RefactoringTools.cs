using System.Collections.Immutable;
using System.ComponentModel;
using McpServices.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace McpServices.Roslyn.Tools;

[McpServerToolType]
public sealed class RefactoringTools(WorkspaceManager workspaces, CodeFixCatalog codeFixes)
{
    private const string DryRunDescription = "Preview only: return the unified diff without writing files (default true). Pass false to apply.";

    [McpServerTool(Name = "rename_symbol", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, Title = "Rename symbol")]
    [Description("Rename a symbol solution-wide (declarations, references, overrides, interface implementations; optionally strings/comments). Returns a unified diff per changed file; with dryRun=false the files are written.")]
    public async Task<object> RenameSymbol(
        [Description("New name.")] string newName,
        [Description("Symbol name (see get_symbol_info).")] string? symbol = null,
        [Description("File path when addressing by position.")] string? file = null,
        [Description("1-based line.")] int? line = null,
        [Description("1-based column (default 1).")] int? column = null,
        [Description("Also rename overloads of a method (default false).")] bool renameOverloads = false,
        [Description("Rename occurrences inside string literals (default false).")] bool renameInStrings = false,
        [Description("Rename occurrences inside comments (default false).")] bool renameInComments = false,
        [Description("Rename the file too when it is named after a renamed type (default false).")] bool renameFile = false,
        [Description(DryRunDescription)] bool dryRun = true,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(newName, "newName");
        if (!SyntaxFacts.IsValidIdentifier(newName.TrimStart('@')))
        {
            throw new ToolException($"'{newName}' is not a valid C# identifier.");
        }

        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = await NavigationTools.ResolveAsync(session, symbol, file, line, column, cancellationToken).ConfigureAwait(false);
        if (!Symbols.IsSourceSymbol(target))
        {
            throw new ToolException($"'{Symbols.FullName(target)}' is declared in metadata and cannot be renamed.");
        }

        if (target.Name == newName)
        {
            throw new ToolException("The symbol already has that name.");
        }

        var renameOptions = new SymbolRenameOptions(RenameOverloads: renameOverloads, RenameInStrings: renameInStrings, RenameInComments: renameInComments, RenameFile: renameFile);
        var renamed = await Renamer.RenameSymbolAsync(session.Solution, target, renameOptions, newName, cancellationToken).ConfigureAwait(false);
        var changes = await DiffAsync(session.Solution, renamed, cancellationToken).ConfigureAwait(false);
        var conflicts = await NewErrorsAsync(session.Solution, renamed, changes, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> written = [];
        if (!dryRun)
        {
            if (conflicts.Count > 0)
            {
                throw new ToolException($"Rename would introduce {conflicts.Count} new compile error(s); not applied. Run with dryRun=true to inspect them.");
            }

            written = await workspaces.ApplyAsync(session, renamed, cancellationToken).ConfigureAwait(false);
        }

        return new
        {
            symbol = Symbols.FullName(target),
            newName,
            dryRun,
            changedFiles = changes.Count,
            changes,
            newErrors = conflicts,
            written,
        };
    }

    [McpServerTool(Name = "format_document", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, Title = "Format document")]
    [Description("Format a file with Roslyn's C# formatter (respects .editorconfig options loaded by the project). Returns a diff; dryRun=false writes it.")]
    public async Task<object> FormatDocument(
        [Description("File path.")] string file,
        [Description(DryRunDescription)] bool dryRun = true,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var document = Symbols.GetDocument(session.Solution, file);
        var formatted = await Formatter.FormatAsync(document, options: null, cancellationToken).ConfigureAwait(false);
        return await FinishAsync(session, formatted.Project.Solution, dryRun, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "organize_usings", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, Title = "Organize usings")]
    [Description("Remove unnecessary using directives (compiler CS8019/IDE0005) and sort the rest (System first, then alphabetical; static usings and aliases last). Returns a diff; dryRun=false writes it.")]
    public async Task<object> OrganizeUsings(
        [Description("File path; omit with project set to organize a whole project.")] string? file = null,
        [Description("Project name (all its files) when file is omitted.")] string? project = null,
        [Description("Remove unused usings (default true).")] bool removeUnused = true,
        [Description("Place System.* namespaces first (default true).")] bool systemFirst = true,
        [Description(DryRunDescription)] bool dryRun = true,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        IEnumerable<Document> documents = file is not null
            ? [Symbols.GetDocument(session.Solution, file)]
            : project is not null
                ? WorkspaceTools.FindProject(session.Solution, project).Documents
                : throw new ToolException("Pass 'file' or 'project'.");

        var solution = session.Solution;
        foreach (var document in documents.Where(d => d.FilePath is not null && !d.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var current = solution.GetDocument(document.Id)!;
            var updated = await OrganizeAsync(current, removeUnused, systemFirst, cancellationToken).ConfigureAwait(false);
            solution = updated.Project.Solution;
        }

        return await FinishAsync(session, solution, dryRun, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "list_code_fixes", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List code fixes")]
    [Description("Diagnostics in a file that have an available code fix, with the fix titles (from Roslyn's built-in fixes and the project's analyzer packages). Use apply_code_fix to apply one.")]
    public async Task<object> ListCodeFixes(
        [Description("File path.")] string file,
        [Description("Only this diagnostic id.")] string? diagnosticId = null,
        [Description("Only diagnostics on this 1-based line.")] int? line = null,
        [Description("Include analyzer diagnostics (default true).")] bool includeAnalyzers = true,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var document = Symbols.GetDocument(session.Solution, file);
        var diagnostics = await DocumentDiagnosticsAsync(document, includeAnalyzers, diagnosticId, line, cancellationToken).ConfigureAwait(false);
        var items = new List<object>();
        foreach (var diagnostic in diagnostics)
        {
            var actions = await ActionsAsync(document, diagnostic, cancellationToken).ConfigureAwait(false);
            if (actions.Count == 0)
            {
                continue;
            }

            var span = diagnostic.Location.GetLineSpan();
            items.Add(new
            {
                diagnostic.Id,
                severity = diagnostic.Severity.ToString().ToLowerInvariant(),
                message = diagnostic.GetMessage(),
                line = span.StartLinePosition.Line + 1,
                column = span.StartLinePosition.Character + 1,
                fixes = actions.Select(a => new { a.Action.Title, provider = a.Provider.GetType().Name }).ToList(),
            });
        }

        return new
        {
            file = document.FilePath,
            diagnosticsWithFixes = items.Count,
            diagnosticsChecked = diagnostics.Count,
            items,
            fixableIds = codeFixes.FixableIds(document.Project).Count,
        };
    }

    [McpServerTool(Name = "apply_code_fix", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, Title = "Apply code fix")]
    [Description("Apply a Roslyn code fix for a diagnostic id in a file (first matching fix, or the one whose title contains fixTitle). With fixAll=true every occurrence of the id in the file is fixed. Returns a diff; dryRun=false writes it.")]
    public async Task<object> ApplyCodeFix(
        [Description("File path.")] string file,
        [Description("Diagnostic id to fix, e.g. CS8019, IDE0005, CA1822, CS0246.")] string diagnosticId,
        [Description("1-based line of the diagnostic (required unless fixAll=true).")] int? line = null,
        [Description("Substring of the fix title to choose between several fixes.")] string? fixTitle = null,
        [Description("Fix every occurrence of the diagnostic in the file (default false).")] bool fixAll = false,
        [Description("Include analyzer diagnostics (default true).")] bool includeAnalyzers = true,
        [Description(DryRunDescription)] bool dryRun = true,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(diagnosticId, "diagnosticId");
        if (!fixAll && line is null)
        {
            throw new ToolException("Pass 'line' (or fixAll=true).");
        }

        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var document = Symbols.GetDocument(session.Solution, file);
        if (codeFixes.ForDiagnostic(document.Project, diagnosticId).Count == 0)
        {
            throw new ToolException($"No code fix provider knows '{diagnosticId}'. list_code_fixes shows what is fixable in this file.");
        }

        var solution = session.Solution;
        var applied = new List<object>();
        var currentDocument = document;
        for (var iteration = 0; iteration < (fixAll ? 200 : 1); iteration++)
        {
            var diagnostics = await DocumentDiagnosticsAsync(currentDocument, includeAnalyzers, diagnosticId, fixAll ? null : line, cancellationToken).ConfigureAwait(false);
            var diagnostic = diagnostics.Count > 0 ? diagnostics[0] : null;
            if (diagnostic is null)
            {
                if (iteration == 0)
                {
                    throw new ToolException(fixAll
                        ? $"No '{diagnosticId}' diagnostics in {Path.GetFileName(document.FilePath)}."
                        : $"No '{diagnosticId}' diagnostic on line {line} of {Path.GetFileName(document.FilePath)}.");
                }

                break;
            }

            var actions = await ActionsAsync(currentDocument, diagnostic, cancellationToken).ConfigureAwait(false);
            var chosen = fixTitle is null
                ? (actions.Count > 0 ? actions[0] : null)
                : actions.Cast<(CodeAction Action, CodeFixProvider Provider)?>().FirstOrDefault(a => a!.Value.Action.Title.Contains(fixTitle, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                if (iteration == 0)
                {
                    throw new ToolException(actions.Count == 0
                        ? $"The registered providers offered no fix for '{diagnosticId}' here."
                        : $"No fix title contains '{fixTitle}'. Available: {string.Join(" | ", actions.Select(a => a.Action.Title))}");
                }

                break;
            }

            var (action, provider) = chosen.Value;
            var operations = await action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
            var change = operations.OfType<ApplyChangesOperation>().FirstOrDefault()
                ?? throw new ToolException($"Fix '{action.Title}' does not produce a solution change and cannot be applied headless.");
            var before = solution;
            solution = change.ChangedSolution;
            applied.Add(new { diagnostic.Id, line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1, fix = action.Title, provider = provider.GetType().Name });
            var next = solution.GetDocument(currentDocument.Id);
            if (next is null || !fixAll || SolutionUnchanged(before, solution))
            {
                break;
            }

            currentDocument = next;
        }

        var result = await FinishAsync(session, solution, dryRun, cancellationToken).ConfigureAwait(false);
        return new { diagnosticId, applied, appliedCount = applied.Count, result.DryRun, result.ChangedFiles, result.Changes, result.Written };
    }

    internal static async Task<Document> OrganizeAsync(Document document, bool removeUnused, bool systemFirst, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax unit)
        {
            return document;
        }

        var unnecessary = new HashSet<TextSpan>();
        if (removeUnused)
        {
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is not null)
            {
                foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken).Where(d => d.Id is "CS8019" or "IDE0005"))
                {
                    unnecessary.Add(diagnostic.Location.SourceSpan);
                }
            }
        }

        // Mark the doomed directives on the original tree first: once usings are rewritten, positions shift
        // and diagnostic spans no longer line up.
        var remove = new SyntaxAnnotation("mcp-remove-using");
        var doomed = unit.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => unnecessary.Any(span => span.IntersectsWith(u.Span))).ToList();
        var annotated = (CompilationUnitSyntax)unit.ReplaceNodes(doomed, (_, rewritten) => rewritten.WithAdditionalAnnotations(remove));

        var newUnit = annotated.WithUsings(Organize(annotated.Usings, remove, systemFirst));
        newUnit = newUnit.ReplaceNodes(
            newUnit.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToList(),
            (_, rewritten) => rewritten.WithUsings(Organize(rewritten.Usings, remove, systemFirst)));

        if (annotated.Usings.Count > 0 && newUnit.Usings.Count == 0)
        {
            // Every top-level using went away: drop the blank line that separated them from what follows.
            var first = newUnit.GetFirstToken(includeZeroWidth: true);
            var trivia = first.LeadingTrivia.SkipWhile(t => t.IsKind(SyntaxKind.EndOfLineTrivia)).ToList();
            newUnit = newUnit.ReplaceToken(first, first.WithLeadingTrivia(SyntaxFactory.TriviaList(trivia)));
        }

        return document.WithSyntaxRoot(newUnit);
    }

    private static SyntaxList<UsingDirectiveSyntax> Organize(SyntaxList<UsingDirectiveSyntax> usings, SyntaxAnnotation remove, bool systemFirst)
    {
        if (usings.Count == 0)
        {
            return usings;
        }

        var kept = usings.Where(u => !u.HasAnnotation(remove)).ToList();
        if (kept.Count == 0)
        {
            return default;
        }

        var leading = usings[0].GetLeadingTrivia();
        var newLine = usings.SelectMany(u => u.GetTrailingTrivia()).FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        if (newLine == default)
        {
            newLine = SyntaxFactory.ElasticLineFeed;
        }

        var ordered = kept
            .OrderBy(u => u.GlobalKeyword.IsKind(SyntaxKind.None) ? 1 : 0)
            .ThenBy(u => u.Alias is not null ? 2 : u.StaticKeyword.IsKind(SyntaxKind.None) ? 0 : 1)
            .ThenBy(u => systemFirst && IsSystem(u) ? 0 : 1)
            .ThenBy(u => u.NamespaceOrType?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .Select((u, i) => u.WithLeadingTrivia(i == 0 ? leading : SyntaxTriviaList.Empty).WithTrailingTrivia(newLine))
            .ToList();

        var last = ordered[^1].WithTrailingTrivia(usings[^1].GetTrailingTrivia());
        ordered[^1] = last;
        return SyntaxFactory.List(ordered);

        static bool IsSystem(UsingDirectiveSyntax u)
        {
            var name = u.NamespaceOrType?.ToString() ?? string.Empty;
            return name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
        }
    }

    internal async Task<IReadOnlyList<(CodeAction Action, CodeFixProvider Provider)>> ActionsAsync(Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var results = new List<(CodeAction, CodeFixProvider)>();
        foreach (var provider in codeFixes.ForDiagnostic(document.Project, diagnostic.Id))
        {
            try
            {
                var context = new CodeFixContext(document, diagnostic, (action, _) => results.Add((action, provider)), cancellationToken);
                await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A misbehaving provider must not hide the fixes of the others.
                _ = ex;
            }
        }

        return results;
    }

    internal static async Task<IReadOnlyList<Diagnostic>> DocumentDiagnosticsAsync(Document document, bool includeAnalyzers, string? id, int? line, CancellationToken cancellationToken)
    {
        var all = (await DiagnosticsTools.CollectAsync(document.Project, includeAnalyzers, cancellationToken).ConfigureAwait(false)).ToList();
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);

        // Hidden per-document diagnostics such as CS8019 (unnecessary using) only exist on the semantic model.
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is not null)
        {
            var known = all.Select(d => (d.Id, d.Location.SourceSpan)).ToHashSet();
            all.AddRange(model.GetDiagnostics(cancellationToken: cancellationToken).Where(d => !known.Contains((d.Id, d.Location.SourceSpan))));
        }

        return all
            .Where(d => d.Location.SourceTree == tree || (d.Location.SourceTree is not null && d.Location.SourceTree.FilePath == document.FilePath))
            .Where(d => id is null || d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .Where(d => line is null || d.Location.GetLineSpan().StartLinePosition.Line + 1 == line)
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToList();
    }

    private static bool SolutionUnchanged(Solution before, Solution after) =>
        !after.GetChanges(before).GetProjectChanges().Any(p => p.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Any());

    internal static async Task<IReadOnlyList<FileChange>> DiffAsync(Solution before, Solution after, CancellationToken cancellationToken)
    {
        var changes = new List<FileChange>();
        foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var oldDocument = before.GetDocument(documentId)!;
                var newDocument = after.GetDocument(documentId)!;
                var oldText = (await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var newText = (await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                if (oldText == newText)
                {
                    continue;
                }

                var path = newDocument.FilePath ?? newDocument.Name;
                changes.Add(new FileChange(path, newDocument.Project.Name, TextDiff.Unified(oldText, newText, path)));
            }

            foreach (var documentId in projectChange.GetAddedDocuments())
            {
                var newDocument = after.GetDocument(documentId)!;
                var newText = (await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var path = newDocument.FilePath ?? newDocument.Name;
                changes.Add(new FileChange(path, newDocument.Project.Name, TextDiff.Unified(string.Empty, newText, path)));
            }
        }

        return changes.OrderBy(c => c.File, StringComparer.Ordinal).ToList();
    }

    private static async Task<IReadOnlyList<object>> NewErrorsAsync(Solution before, Solution after, IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return [];
        }

        var projects = changes.Select(c => c.Project).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var result = new List<object>();
        foreach (var project in after.Projects.Where(p => projects.Contains(p.Name)))
        {
            var oldProject = before.Projects.FirstOrDefault(p => p.Id == project.Id);
            var oldErrors = oldProject is null ? [] : (await oldProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false))?.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id + ":" + d.GetMessage()).ToHashSet(StringComparer.Ordinal) ?? [];
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                if (!oldErrors.Contains(diagnostic.Id + ":" + diagnostic.GetMessage()))
                {
                    result.Add(DiagnosticsTools.Describe(diagnostic, after));
                }
            }
        }

        return result.Take(50).ToList();
    }

    private async Task<ApplyResult> FinishAsync(WorkspaceSession session, Solution solution, bool dryRun, CancellationToken cancellationToken)
    {
        var changes = await DiffAsync(session.Solution, solution, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> written = [];
        if (!dryRun && changes.Count > 0)
        {
            written = await workspaces.ApplyAsync(session, solution, cancellationToken).ConfigureAwait(false);
        }

        return new ApplyResult(dryRun, changes.Count, changes, written);
    }

    public sealed record FileChange(string File, string Project, string Diff);

    public sealed record ApplyResult(bool DryRun, int ChangedFiles, IReadOnlyList<FileChange> Changes, IReadOnlyList<string> Written);
}
