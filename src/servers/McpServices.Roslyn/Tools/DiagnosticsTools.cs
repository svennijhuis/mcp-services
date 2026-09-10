using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using System.Xml.Linq;
using McpServices.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace McpServices.Roslyn.Tools;

[McpServerToolType]
public sealed class DiagnosticsTools(WorkspaceManager workspaces, RoslynOptions options)
{
    [McpServerTool(Name = "get_diagnostics", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Diagnostics")]
    [Description("Compiler (and optionally analyzer) diagnostics for the solution, one project or one file, filtered by minimum severity. Grouped counts per id are included so large lists stay useful.")]
    public async Task<object> GetDiagnostics(
        [Description("Scope: solution | project | file (default solution).")] string? scope = null,
        [Description("Project name when scope=project (or to narrow scope=file).")] string? project = null,
        [Description("File path when scope=file.")] string? file = null,
        [Description("Minimum severity: hidden | info | warning | error (default warning).")] string? minSeverity = null,
        [Description("Run the project's analyzers too (slower; default false).")] bool includeAnalyzers = false,
        [Description("Only these diagnostic ids (e.g. ['CS8602','CA1822']).")] string[]? ids = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var scopeValue = (scope ?? (file is not null ? "file" : project is not null ? "project" : "solution")).ToLowerInvariant();
        ToolGuard.OneOf(scopeValue, "scope", "solution", "project", "file");
        var minimum = ParseSeverity(minSeverity ?? "warning");
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);

        IEnumerable<Project> projects;
        Document? document = null;
        switch (scopeValue)
        {
            case "file":
                document = Symbols.GetDocument(session.Solution, file ?? throw new ToolException("scope=file requires 'file'."));
                projects = [document.Project];
                break;
            case "project":
                projects = [WorkspaceTools.FindProject(session.Solution, project ?? throw new ToolException("scope=project requires 'project'."))];
                break;
            default:
                projects = session.Solution.Projects;
                break;
        }

        var idSet = ids is { Length: > 0 } ? new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase) : null;
        var all = new List<Diagnostic>();
        foreach (var p in projects)
        {
            var diagnostics = await CollectAsync(p, includeAnalyzers, cancellationToken).ConfigureAwait(false);
            all.AddRange(diagnostics.Where(d => d.Severity >= minimum && !d.IsSuppressed && (idSet is null || idSet.Contains(d.Id))
                && (document is null || (d.Location.SourceTree is not null && d.Location.SourceTree.FilePath == document.FilePath))));
        }

        var ordered = all
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .Take(options.MaxResults)
            .Select(d => Describe(d, session.Solution))
            .ToList();

        var page = Paging.Page(ordered, pageToken, pageSize);
        return new
        {
            workspaceId = session.Id,
            scope = scopeValue,
            summary = new
            {
                errors = all.Count(d => d.Severity == DiagnosticSeverity.Error),
                warnings = all.Count(d => d.Severity == DiagnosticSeverity.Warning),
                info = all.Count(d => d.Severity == DiagnosticSeverity.Info),
                hidden = all.Count(d => d.Severity == DiagnosticSeverity.Hidden),
            },
            byId = all.GroupBy(d => d.Id, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Take(30).Select(g => new { id = g.Key, count = g.Count(), title = g.First().Descriptor.Title.ToString() }),
            diagnostics = page.Items,
            totalCount = page.TotalCount,
            nextPageToken = page.NextPageToken,
            needsReload = session.NeedsReload,
        };
    }

    [McpServerTool(Name = "compile_check", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Compile check")]
    [Description("Fast in-memory compile of the workspace (no build output): reports whether every project compiles and lists the errors. Much faster than dotnet build.")]
    public async Task<object> CompileCheck(
        [Description("Project name filter.")] string? project = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Maximum errors to return (default 100).")] int? maxErrors = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var projects = project is null ? session.Solution.Projects : [WorkspaceTools.FindProject(session.Solution, project)];
        var results = new List<object>();
        var errors = new List<object>();
        var cap = Math.Clamp(maxErrors ?? 100, 1, 2000);
        var allSucceeded = true;

        foreach (var p in projects)
        {
            var compilation = await p.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                allSucceeded = false;
                results.Add(new { project = p.Name, success = false, errors = 0, warnings = 0, note = "no compilation" });
                continue;
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken);
            var projectErrors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            allSucceeded &= projectErrors.Count == 0;
            results.Add(new { project = p.Name, success = projectErrors.Count == 0, errors = projectErrors.Count, warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning) });
            foreach (var error in projectErrors)
            {
                if (errors.Count >= cap)
                {
                    break;
                }

                errors.Add(Describe(error, session.Solution));
            }
        }

        return new
        {
            workspaceId = session.Id,
            success = allSucceeded,
            projects = results,
            errors,
            truncated = errors.Count >= cap,
            needsReload = session.NeedsReload,
        };
    }

    [McpServerTool(Name = "find_unused_symbols", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Unused symbols")]
    [Description("Declared types and members that have no references anywhere in the workspace. Public API of library projects is included only when includePublic=true; entry points, overrides, interface implementations, test methods and attributed members are skipped.")]
    public async Task<object> FindUnusedSymbols(
        [Description("Project name filter.")] string? project = null,
        [Description("Include public/protected symbols (default false: only internal/private are considered unused).")] bool includePublic = false,
        [Description("Kinds to check (default: class, interface, struct, enum, record, method, property, field, event).")] string[]? kinds = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var projects = project is null ? session.Solution.Projects.ToList() : [WorkspaceTools.FindProject(session.Solution, project)];
        var kindSet = kinds is { Length: > 0 } ? new HashSet<string>(kinds.Select(k => k.ToLowerInvariant()), StringComparer.Ordinal) : null;

        var candidates = new List<ISymbol>();
        foreach (var p in projects)
        {
            var compilation = await p.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in WorkspaceTools.SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                if (IsCandidate(type, includePublic, kindSet))
                {
                    candidates.Add(type);
                }

                foreach (var member in type.GetMembers())
                {
                    if (IsCandidate(member, includePublic, kindSet))
                    {
                        candidates.Add(member);
                    }
                }
            }
        }

        var unused = new List<object>();
        foreach (var candidate in candidates.Take(options.MaxResults * 2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var references = await SymbolFinder.FindReferencesAsync(candidate, session.Solution, cancellationToken).ConfigureAwait(false);
            var count = references.Sum(r => r.Locations.Count(l => !l.IsImplicit && !l.IsCandidateLocation));
            if (count == 0)
            {
                var summary = Symbols.Summarize(candidate, session.Solution, includeDocs: false);
                unused.Add(new { summary.FullName, summary.Kind, summary.Accessibility, summary.Signature, location = summary.Locations.Count > 0 ? summary.Locations[0] : null });
            }

            if (unused.Count >= options.MaxResults)
            {
                break;
            }
        }

        var page = Paging.Page(unused, pageToken, pageSize);
        return new { workspaceId = session.Id, checkedSymbols = Math.Min(candidates.Count, options.MaxResults * 2), unused = page.Items, totalCount = page.TotalCount, nextPageToken = page.NextPageToken };
    }

    [McpServerTool(Name = "get_complexity_metrics", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Complexity metrics")]
    [Description("Per-method metrics: cyclomatic complexity, nesting depth, lines, parameters, statements; plus per-type member/line counts. Sorted by complexity so hotspots come first.")]
    public async Task<object> GetComplexityMetrics(
        [Description("Project name filter.")] string? project = null,
        [Description("File path filter.")] string? file = null,
        [Description("Only report methods with cyclomatic complexity >= this (default 1).")] int? minComplexity = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        IEnumerable<Document> documents;
        if (file is not null)
        {
            documents = [Symbols.GetDocument(session.Solution, file)];
        }
        else
        {
            var projects = project is null ? session.Solution.Projects : [WorkspaceTools.FindProject(session.Solution, project)];
            documents = projects.SelectMany(p => p.Documents);
        }

        var threshold = Math.Max(1, minComplexity ?? 1);
        var methods = new List<MethodMetrics>();
        var types = new Dictionary<string, TypeMetrics>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (document.FilePath is null || document.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
            {
                continue;
            }

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(type, cancellationToken);
                if (symbol is null)
                {
                    continue;
                }

                var name = Symbols.FullName(symbol);
                var span = type.GetLocation().GetLineSpan();
                var existing = types.GetValueOrDefault(name) ?? new TypeMetrics(name, Symbols.Kind(symbol), 0, 0, 0, 0, 0, document.FilePath);
                types[name] = existing with
                {
                    Lines = existing.Lines + span.EndLinePosition.Line - span.StartLinePosition.Line + 1,
                    Members = existing.Members + type.Members.Count(m => m is not TypeDeclarationSyntax),
                    Methods = existing.Methods + type.Members.Count(m => m is BaseMethodDeclarationSyntax),
                    Fields = existing.Fields + type.Members.Count(m => m is FieldDeclarationSyntax),
                    Properties = existing.Properties + type.Members.Count(m => m is PropertyDeclarationSyntax),
                };
            }

            foreach (var node in root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax { Body: not null } or AccessorDeclarationSyntax { ExpressionBody: not null }))
            {
                var symbol = model.GetDeclaredSymbol(node, cancellationToken);
                if (symbol is null)
                {
                    continue;
                }

                var body = node switch
                {
                    BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
                    AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                    _ => null,
                };
                if (body is null)
                {
                    continue;
                }

                var complexity = CyclomaticComplexity(body);
                if (complexity < threshold)
                {
                    continue;
                }

                var span = node.GetLocation().GetLineSpan();
                var parameters = node switch
                {
                    BaseMethodDeclarationSyntax m => m.ParameterList.Parameters.Count,
                    LocalFunctionStatementSyntax l => l.ParameterList.Parameters.Count,
                    _ => 0,
                };
                methods.Add(new MethodMetrics(
                    Symbols.FullName(symbol),
                    Symbols.Kind(symbol),
                    complexity,
                    MaxNesting(body),
                    span.EndLinePosition.Line - span.StartLinePosition.Line + 1,
                    body.DescendantNodes().OfType<StatementSyntax>().Count(s => s is not BlockSyntax),
                    parameters,
                    document.FilePath,
                    span.StartLinePosition.Line + 1));
            }
        }

        var ordered = methods.OrderByDescending(m => m.CyclomaticComplexity).ThenByDescending(m => m.Lines).Take(options.MaxResults).ToList();
        var page = Paging.Page(ordered, pageToken, pageSize);
        return new
        {
            workspaceId = session.Id,
            summary = new
            {
                methods = methods.Count,
                averageComplexity = methods.Count == 0 ? 0 : Math.Round(methods.Average(m => m.CyclomaticComplexity), 2),
                maxComplexity = methods.Count == 0 ? 0 : methods.Max(m => m.CyclomaticComplexity),
                over10 = methods.Count(m => m.CyclomaticComplexity > 10),
                over20 = methods.Count(m => m.CyclomaticComplexity > 20),
                types = types.Count,
            },
            types = types.Values.OrderByDescending(t => t.Lines).Take(50),
            methods = page.Items,
            totalCount = page.TotalCount,
            nextPageToken = page.NextPageToken,
        };
    }

    [McpServerTool(Name = "get_namespace_dependencies", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Namespace dependencies")]
    [Description("Which namespaces depend on which (from type usages in source), with reference counts and detected cycles. Useful for layering/architecture checks.")]
    public async Task<object> GetNamespaceDependencies(
        [Description("Project name filter.")] string? project = null,
        [Description("Only include namespaces declared in the workspace (default true).")] bool sourceOnly = true,
        [Description("Namespace depth to aggregate to (e.g. 2 folds 'A.B.C' into 'A.B'; default: full namespaces).")] int? depth = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var projects = project is null ? session.Solution.Projects : [WorkspaceTools.FindProject(session.Solution, project)];
        var edges = new Dictionary<(string From, string To), int>();
        var sourceNamespaces = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in projects)
        {
            var compilation = await p.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in WorkspaceTools.SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                sourceNamespaces.Add(Fold(NamespaceOf(type), depth));
            }

            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                foreach (var typeDeclaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    var declared = model.GetDeclaredSymbol(typeDeclaration, cancellationToken);
                    if (declared is null)
                    {
                        continue;
                    }

                    var from = Fold(NamespaceOf(declared), depth);
                    foreach (var node in typeDeclaration.DescendantNodes().Where(n => n is IdentifierNameSyntax or GenericNameSyntax or ObjectCreationExpressionSyntax or MemberAccessExpressionSyntax))
                    {
                        var info = model.GetSymbolInfo(node, cancellationToken);
                        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                        var namedType = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                        if (namedType is null || (sourceOnly && !Symbols.IsSourceSymbol(namedType)))
                        {
                            continue;
                        }

                        var to = Fold(NamespaceOf(namedType), depth);
                        if (to == from)
                        {
                            continue;
                        }

                        edges[(from, to)] = edges.GetValueOrDefault((from, to)) + 1;
                    }
                }
            }
        }

        var graph = edges.Keys.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var cycles = FindCycles(graph);
        return new
        {
            workspaceId = session.Id,
            namespaces = sourceNamespaces.OrderBy(n => n, StringComparer.Ordinal),
            dependencies = edges.OrderBy(e => e.Key.From, StringComparer.Ordinal).ThenByDescending(e => e.Value).Select(e => new { from = e.Key.From, to = e.Key.To, references = e.Value }),
            cycles,
            fanOut = graph.OrderByDescending(g => g.Value.Count).Take(20).Select(g => new { ns = g.Key, dependsOn = g.Value.Count }),
        };
    }

    [McpServerTool(Name = "get_nuget_dependencies", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "NuGet dependencies")]
    [Description("Package references per project (from the .csproj and Directory.Packages.props) with resolved versions from project.assets.json when a restore has run.")]
    public async Task<object> GetNugetDependencies(
        [Description("Project name filter.")] string? project = null,
        [Description("Include transitive packages from project.assets.json (default false).")] bool includeTransitive = false,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var projects = project is null ? session.Solution.Projects : [WorkspaceTools.FindProject(session.Solution, project)];
        var results = new List<object>();
        foreach (var p in projects.Where(p => p.FilePath is not null).GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var directory = Path.GetDirectoryName(p.FilePath!)!;
            var central = ReadCentralVersions(directory);
            var declared = new List<(string Id, string? Version)>();
            try
            {
                var xml = XDocument.Load(p.FilePath!);
                foreach (var reference in xml.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
                {
                    var id = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
                    if (id is null)
                    {
                        continue;
                    }

                    var version = reference.Attribute("Version")?.Value ?? reference.Element("Version")?.Value ?? reference.Attribute("VersionOverride")?.Value ?? central.GetValueOrDefault(id);
                    declared.Add((id, version));
                }
            }
            catch (System.Xml.XmlException ex)
            {
                results.Add(new { project = p.Name, error = "Cannot parse project file: " + ex.Message });
                continue;
            }

            var resolved = ReadResolvedPackages(directory);
            var restored = File.Exists(Path.Combine(directory, "obj", "project.assets.json"));
            var packages = declared.Select(d => new
            {
                id = d.Id,
                requested = d.Version,
                resolved = resolved.GetValueOrDefault(d.Id),
                centrallyManaged = central.ContainsKey(d.Id),
            }).OrderBy(x => x.id, StringComparer.OrdinalIgnoreCase).ToList();

            var declaredIds = new HashSet<string>(declared.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            results.Add(new
            {
                project = p.Name,
                projectFile = p.FilePath,
                targetFramework = p.Name.Contains('(') ? p.Name[(p.Name.IndexOf('(', StringComparison.Ordinal) + 1)..^1] : null,
                packages,
                transitive = includeTransitive ? resolved.Where(r => !declaredIds.Contains(r.Key)).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => new { id = r.Key, version = r.Value }).ToList() : null,
                restored,
            });
        }

        return new { workspaceId = session.Id, projects = results };
    }

    internal static async Task<ImmutableArray<Diagnostic>> CollectAsync(Project project, bool includeAnalyzers, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return [];
        }

        if (!includeAnalyzers)
        {
            return compilation.GetDiagnostics(cancellationToken);
        }

        var analyzers = project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp)).ToImmutableArray();
        if (analyzers.Length == 0)
        {
            return compilation.GetDiagnostics(cancellationToken);
        }

        var withAnalyzers = compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(project.AnalyzerOptions, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false));
        return await withAnalyzers.GetAllDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static object Describe(Diagnostic diagnostic, Solution solution)
    {
        var location = diagnostic.Location.IsInSource ? Symbols.Location(diagnostic.Location, solution, 1) : null;
        return new
        {
            diagnostic.Id,
            severity = diagnostic.Severity.ToString().ToLowerInvariant(),
            message = diagnostic.GetMessage(),
            category = diagnostic.Descriptor.Category,
            location?.File,
            location?.Line,
            location?.Column,
            location?.EndLine,
            location?.EndColumn,
            location?.Project,
            snippet = location?.Snippet?.Trim(),
            helpLink = string.IsNullOrEmpty(diagnostic.Descriptor.HelpLinkUri) ? null : diagnostic.Descriptor.HelpLinkUri,
        };
    }

    internal static DiagnosticSeverity ParseSeverity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "hidden" => DiagnosticSeverity.Hidden,
        "info" or "information" or "suggestion" => DiagnosticSeverity.Info,
        "warning" or "warn" => DiagnosticSeverity.Warning,
        "error" => DiagnosticSeverity.Error,
        _ => throw new ToolException($"Unknown severity '{value}'. Use hidden, info, warning or error."),
    };

    private static bool IsCandidate(ISymbol symbol, bool includePublic, HashSet<string>? kinds)
    {
        if (symbol.IsImplicitlyDeclared || !Symbols.IsSourceSymbol(symbol))
        {
            return false;
        }

        if (symbol is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.Constructor) })
        {
            return false;
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, Parameters.Length: 0 } || symbol is IMethodSymbol { Name: "Main" or "Dispose" or "Equals" or "GetHashCode" or "ToString" })
        {
            return false;
        }

        if (symbol.IsOverride || symbol.GetAttributes().Length > 0 || symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } || symbol.ContainingType?.TypeKind == TypeKind.Enum || symbol.ContainingType?.TypeKind == TypeKind.Interface)
        {
            return false;
        }

        if (symbol.ContainingType is { } owner && owner.AllInterfaces.Any(i => i.GetMembers().Any(m => SymbolEqualityComparer.Default.Equals(owner.FindImplementationForInterfaceMember(m), symbol))))
        {
            return false;
        }

        var visible = symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
        if (visible && !includePublic)
        {
            return false;
        }

        if (visible && symbol.ContainingType is not null && symbol.ContainingType.DeclaredAccessibility is Accessibility.Public && !includePublic)
        {
            return false;
        }

        if (kinds is not null && !kinds.Contains(Symbols.Kind(symbol).Replace(" ", string.Empty, StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    public static int CyclomaticComplexity(SyntaxNode body)
    {
        var complexity = 1;
        foreach (var node in body.DescendantNodesAndSelf(descendIntoChildren: n => n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) || n == body))
        {
            switch (node)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case DoStatementSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                case ConditionalAccessExpressionSyntax:
                case WhenClauseSyntax:
                    complexity++;
                    break;
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression) || b.IsKind(SyntaxKind.CoalesceExpression):
                    complexity++;
                    break;
                case BinaryPatternSyntax:
                    complexity++;
                    break;
            }
        }

        return complexity;
    }

    public static int MaxNesting(SyntaxNode body)
    {
        var max = 0;
        Visit(body, 0);
        return max;

        void Visit(SyntaxNode node, int depth)
        {
            foreach (var child in node.ChildNodes())
            {
                var nested = child is IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or DoStatementSyntax or SwitchStatementSyntax or TryStatementSyntax or UsingStatementSyntax or LockStatementSyntax or AnonymousFunctionExpressionSyntax;
                var next = nested ? depth + 1 : depth;
                if (next > max)
                {
                    max = next;
                }

                Visit(child, next);
            }
        }
    }

    private static string NamespaceOf(ISymbol symbol) => symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "<global>";

    private static string Fold(string ns, int? depth)
    {
        if (depth is null or < 1)
        {
            return ns;
        }

        var parts = ns.Split('.');
        return parts.Length <= depth ? ns : string.Join('.', parts.Take(depth.Value));
    }

    private static List<List<string>> FindCycles(Dictionary<string, HashSet<string>> graph)
    {
        var cycles = new List<List<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in graph.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var stack = new List<string>();
            Dfs(start, start, stack, new HashSet<string>(StringComparer.Ordinal));
            if (cycles.Count >= 25)
            {
                break;
            }
        }

        return cycles;

        void Dfs(string start, string current, List<string> path, HashSet<string> visiting)
        {
            if (!visiting.Add(current) || path.Count > 12)
            {
                return;
            }

            path.Add(current);
            foreach (var next in graph.GetValueOrDefault(current) ?? [])
            {
                if (next == start && path.Count > 1)
                {
                    var key = string.Join("\u0001", path.OrderBy(p => p, StringComparer.Ordinal));
                    if (seen.Add(key))
                    {
                        cycles.Add([.. path, start]);
                    }
                }
                else if (string.CompareOrdinal(next, start) > 0)
                {
                    Dfs(start, next, path, visiting);
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(current);
        }
    }

    private static Dictionary<string, string> ReadCentralVersions(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var props = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (!File.Exists(props))
            {
                continue;
            }

            try
            {
                foreach (var element in XDocument.Load(props).Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
                {
                    var id = element.Attribute("Include")?.Value;
                    var version = element.Attribute("Version")?.Value;
                    if (id is not null && version is not null)
                    {
                        result.TryAdd(id, version);
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                // Ignore malformed props; the project still lists its references.
            }

            break;
        }

        return result;
    }

    private static Dictionary<string, string> ReadResolvedPackages(string projectDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assets = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(assets))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assets));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries))
            {
                return result;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.TryGetProperty("type", out var type) && type.GetString() != "package")
                {
                    continue;
                }

                var slash = library.Name.IndexOf('/', StringComparison.Ordinal);
                if (slash > 0)
                {
                    result[library.Name[..slash]] = library.Name[(slash + 1)..];
                }
            }
        }
        catch (JsonException)
        {
            // A half-written assets file during restore is not an error worth surfacing.
        }

        return result;
    }

    private sealed record MethodMetrics(string Name, string Kind, int CyclomaticComplexity, int MaxNesting, int Lines, int Statements, int Parameters, string File, int Line);

    private sealed record TypeMetrics(string Name, string Kind, int Lines, int Members, int Methods, int Fields, int Properties, string File);
}
