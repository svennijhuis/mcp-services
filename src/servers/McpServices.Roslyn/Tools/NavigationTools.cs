using System.ComponentModel;
using McpServices.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace McpServices.Roslyn.Tools;

[McpServerToolType]
public sealed class NavigationTools(WorkspaceManager workspaces, RoslynOptions options)
{
    private const string SymbolDescription = "Symbol name: 'Namespace.Type', 'Type', 'Type.Member' or a bare member name. Alternatively pass file + line (+ column) to pick the symbol at a position.";

    [McpServerTool(Name = "find_symbols", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find symbols")]
    [Description("Search declared symbols (types and members) by name across the workspace. Supports exact, prefix and contains matching, and filtering by kind (class, interface, struct, enum, record, method, property, field, event, delegate, type).")]
    public async Task<object> FindSymbols(
        [Description("Name or fragment to search for.")] string query,
        [Description("Match mode: exact | prefix | contains (default contains). Case-insensitive.")] string? matchMode = null,
        [Description("Kind filter, e.g. class, interface, method, property, type (any named type).")] string? kind = null,
        [Description("Project name filter.")] string? project = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(query, "query");
        var mode = (matchMode ?? "contains").ToLowerInvariant();
        ToolGuard.OneOf(mode, "matchMode", "exact", "prefix", "contains");
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var needle = query.Trim();

        Func<string, bool> predicate = mode switch
        {
            "exact" => name => name.Equals(needle, StringComparison.OrdinalIgnoreCase),
            "prefix" => name => name.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
            _ => name => name.Contains(needle, StringComparison.OrdinalIgnoreCase),
        };

        var declarations = project is null
            ? await SymbolFinder.FindSourceDeclarationsAsync(session.Solution, predicate, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false)
            : await SymbolFinder.FindSourceDeclarationsAsync(WorkspaceTools.FindProject(session.Solution, project), predicate, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false);

        var results = declarations
            .Where(s => s is not INamespaceSymbol && !s.IsImplicitlyDeclared)
            .Where(s => s is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
            .Where(s => MatchesKind(s, kind))
            .GroupBy(Symbols.FullName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => s.Name.Equals(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(s => s.Name.Length)
            .ThenBy(Symbols.FullName, StringComparer.Ordinal)
            .Take(options.MaxResults)
            .Select(s => Symbols.Summarize(s, session.Solution, includeDocs: false))
            .ToList();

        return Paging.Page(results, pageToken, pageSize);
    }

    [McpServerTool(Name = "get_file_symbols", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "File symbols")]
    [Description("Outline of one source file: every type and member declared in it with kind, signature and line range (like a document outline).")]
    public async Task<object> GetFileSymbols(
        [Description("File path (absolute, or relative to a project directory).")] string file,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Include private members (default true).")] bool includePrivate = true,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var document = Symbols.GetDocument(session.Solution, file);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) ?? throw new ToolException("No semantic model for this document.");
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) ?? throw new ToolException("No syntax root for this document.");
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

        var items = new List<object>();
        foreach (var node in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (node is GlobalStatementSyntax or IncompleteMemberSyntax)
            {
                continue;
            }

            var symbols = node switch
            {
                FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, cancellationToken)),
                EventFieldDeclarationSyntax e => e.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, cancellationToken)),
                BaseNamespaceDeclarationSyntax => [],
                _ => [model.GetDeclaredSymbol(node, cancellationToken)],
            };

            foreach (var symbol in symbols.OfType<ISymbol>())
            {
                if (!includePrivate && symbol.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                var span = node.GetLocation().GetLineSpan();
                items.Add(new
                {
                    symbol.Name,
                    fullName = Symbols.FullName(symbol),
                    kind = Symbols.Kind(symbol),
                    accessibility = Symbols.Accessibility(symbol.DeclaredAccessibility),
                    signature = symbol.ToDisplayString(Symbols.SignatureFormat),
                    containingType = symbol.ContainingType?.ToDisplayString(Symbols.FullNameFormat),
                    line = span.StartLinePosition.Line + 1,
                    endLine = span.EndLinePosition.Line + 1,
                    depth = node.Ancestors().Count(a => a is TypeDeclarationSyntax),
                });
            }
        }

        return new
        {
            file = document.FilePath,
            project = document.Project.Name,
            lines = text.Lines.Count,
            usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.ToString().TrimEnd(';').Replace("using ", string.Empty, StringComparison.Ordinal)).ToList(),
            symbols = items,
        };
    }

    [McpServerTool(Name = "get_type_members", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Type members")]
    [Description("Members of a type (fields, properties, methods, events, nested types) with signatures; optionally including inherited members.")]
    public async Task<object> GetTypeMembers(
        [Description("Type name, e.g. 'MyApp.Services.OrderService' or 'OrderService'.")] string type,
        [Description("Include inherited members from base types (default false).")] bool includeInherited = false,
        [Description("Include private members (default true).")] bool includePrivate = true,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var symbol = await Symbols.ResolveByNameAsync(session.Solution, type, "type", cancellationToken).ConfigureAwait(false);
        if (symbol is not INamedTypeSymbol named)
        {
            throw new ToolException($"'{type}' is a {Symbols.Kind(symbol)}, not a type.");
        }

        var members = new List<object>();
        for (var current = named; current is not null && current.SpecialType != SpecialType.System_Object; current = includeInherited ? current.BaseType : null)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
                {
                    continue;
                }

                if (!includePrivate && member.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                var summary = Symbols.Summarize(member, session.Solution, includeDocs: false);
                members.Add(new
                {
                    summary.Name,
                    summary.Kind,
                    summary.Accessibility,
                    summary.Signature,
                    summary.Modifiers,
                    declaredIn = SymbolEqualityComparer.Default.Equals(current, named) ? null : current.ToDisplayString(Symbols.FullNameFormat),
                    location = summary.Locations.Count > 0 ? summary.Locations[0] : null,
                });
            }
        }

        return new
        {
            type = Symbols.Summarize(named, session.Solution),
            baseType = named.BaseType is { SpecialType: not SpecialType.System_Object } b ? b.ToDisplayString(Symbols.FullNameFormat) : null,
            interfaces = named.AllInterfaces.Select(i => i.ToDisplayString(Symbols.FullNameFormat)).ToList(),
            members,
        };
    }

    [McpServerTool(Name = "get_symbol_info", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Symbol info")]
    [Description("Everything about one symbol: kind, signature, accessibility, documentation, declaring locations, containing type/namespace, and optionally its source text. Address it by name or by file position.")]
    public async Task<object> GetSymbolInfo(
        [Description(SymbolDescription)] string? symbol = null,
        [Description("File path when addressing by position.")] string? file = null,
        [Description("1-based line when addressing by position.")] int? line = null,
        [Description("1-based column when addressing by position (default 1).")] int? column = null,
        [Description("Include the declaration source text (default false).")] bool includeSource = false,
        [Description("Maximum source lines when includeSource=true (default 120).")] int? maxSourceLines = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = await ResolveAsync(session, symbol, file, line, column, cancellationToken).ConfigureAwait(false);
        var summary = Symbols.Summarize(target, session.Solution, includeDocs: true, snippetLines: 0);

        object? typeInfo = null;
        if (target is INamedTypeSymbol named)
        {
            typeInfo = new
            {
                baseType = named.BaseType is { SpecialType: not SpecialType.System_Object } b ? b.ToDisplayString(Symbols.FullNameFormat) : null,
                interfaces = named.Interfaces.Select(i => i.ToDisplayString(Symbols.FullNameFormat)).ToList(),
                typeParameters = named.TypeParameters.Select(t => t.Name).ToList(),
                memberCount = named.GetMembers().Count(m => !m.IsImplicitlyDeclared),
                isGeneric = named.IsGenericType,
                isStatic = named.IsStatic,
            };
        }

        object? methodInfo = null;
        if (target is IMethodSymbol method)
        {
            methodInfo = new
            {
                returnType = method.ReturnType.ToDisplayString(Symbols.FullNameFormat),
                parameters = method.Parameters.Select(p => new { p.Name, type = p.Type.ToDisplayString(Symbols.FullNameFormat), p.IsOptional, hasDefault = p.HasExplicitDefaultValue, refKind = p.RefKind.ToString().ToLowerInvariant() }).ToList(),
                typeParameters = method.TypeParameters.Select(t => t.Name).ToList(),
                overrides = method.OverriddenMethod?.ToDisplayString(Symbols.FullNameFormat),
                implements = method.ContainingType.AllInterfaces
                    .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
                    .Where(im => SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(im), method))
                    .Select(im => im.ToDisplayString(Symbols.FullNameFormat))
                    .ToList(),
            };
        }

        object? propertyInfo = null;
        if (target is IPropertySymbol property)
        {
            propertyInfo = new
            {
                type = property.Type.ToDisplayString(Symbols.FullNameFormat),
                hasGetter = property.GetMethod is not null,
                hasSetter = property.SetMethod is not null,
                isInit = property.SetMethod?.IsInitOnly ?? false,
                overrides = property.OverriddenProperty?.ToDisplayString(Symbols.FullNameFormat),
            };
        }

        object? fieldInfo = null;
        if (target is IFieldSymbol field)
        {
            fieldInfo = new
            {
                type = field.Type.ToDisplayString(Symbols.FullNameFormat),
                constantValue = field.HasConstantValue ? field.ConstantValue?.ToString() : null,
                field.IsReadOnly,
                field.IsConst,
            };
        }

        return new
        {
            summary.Name,
            summary.FullName,
            summary.Kind,
            summary.ContainingType,
            summary.Namespace,
            summary.Accessibility,
            summary.Signature,
            summary.Modifiers,
            summary.Documentation,
            summary.Locations,
            summary.Project,
            summary.FromMetadata,
            assembly = target.ContainingAssembly?.Name,
            attributes = target.GetAttributes().Select(a => a.AttributeClass?.ToDisplayString(Symbols.FullNameFormat)).Where(a => a is not null).ToList(),
            type = typeInfo,
            method = methodInfo,
            property = propertyInfo,
            field = fieldInfo,
            source = includeSource ? await Symbols.SourceAsync(target, Math.Clamp(maxSourceLines ?? 120, 5, 2000), cancellationToken).ConfigureAwait(false) : null,
        };
    }

    [McpServerTool(Name = "go_to_definition", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Go to definition")]
    [Description("Declaration location(s) of the symbol at a position or by name. For metadata symbols (framework/NuGet) the assembly is reported instead of a file.")]
    public async Task<object> GoToDefinition(
        [Description(SymbolDescription)] string? symbol = null,
        [Description("File path when addressing by position.")] string? file = null,
        [Description("1-based line.")] int? line = null,
        [Description("1-based column (default 1).")] int? column = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = await ResolveAsync(session, symbol, file, line, column, cancellationToken).ConfigureAwait(false);
        var definition = target;
        if (target is IMethodSymbol { ReducedFrom: { } reduced })
        {
            definition = reduced;
        }

        definition = definition.OriginalDefinition;
        var source = await SymbolFinder.FindSourceDefinitionAsync(definition, session.Solution, cancellationToken).ConfigureAwait(false) ?? definition;
        var summary = Symbols.Summarize(source, session.Solution, includeDocs: true, snippetLines: 3);
        return new
        {
            summary.FullName,
            summary.Kind,
            summary.Signature,
            summary.Documentation,
            definitions = summary.Locations,
            summary.FromMetadata,
            assembly = summary.FromMetadata ? source.ContainingAssembly?.ToDisplayString() : null,
            partialDeclarations = summary.Locations.Count,
        };
    }

    [McpServerTool(Name = "find_references", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find references")]
    [Description("All references to a symbol across the workspace, grouped per referenced definition (includes overrides/interface implementations), with the source line of each reference. Paginated.")]
    public async Task<object> FindReferences(
        [Description(SymbolDescription)] string? symbol = null,
        [Description("File path when addressing by position.")] string? file = null,
        [Description("1-based line.")] int? line = null,
        [Description("1-based column (default 1).")] int? column = null,
        [Description("Include the declaration locations themselves (default false).")] bool includeDeclarations = false,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = await ResolveAsync(session, symbol, file, line, column, cancellationToken).ConfigureAwait(false);
        var referenced = await SymbolFinder.FindReferencesAsync(target, session.Solution, cancellationToken).ConfigureAwait(false);

        var items = new List<object>();
        foreach (var group in referenced)
        {
            var definitionName = Symbols.FullName(group.Definition);
            if (includeDeclarations)
            {
                foreach (var location in group.Definition.Locations.Where(l => l.IsInSource))
                {
                    var loc = Symbols.Location(location, session.Solution, 1);
                    items.Add(new { definition = definitionName, kind = "declaration", loc.File, loc.Line, loc.Column, loc.Project, loc.Snippet });
                }
            }

            foreach (var reference in group.Locations.Where(l => !l.IsImplicit))
            {
                var loc = Symbols.Location(reference, 1);
                items.Add(new
                {
                    definition = definitionName,
                    kind = reference.IsImplicit ? "implicit" : "reference",
                    loc.File,
                    loc.Line,
                    loc.Column,
                    loc.Project,
                    loc.Snippet,
                });
            }
        }

        var ordered = items.OrderBy(i => ToolJson.Serialize(i), StringComparer.Ordinal).Take(options.MaxResults).ToList();
        var page = Paging.Page(ordered, pageToken, pageSize);
        return new
        {
            symbol = Symbols.FullName(target),
            kind = Symbols.Kind(target),
            definitions = referenced.Select(g => Symbols.FullName(g.Definition)).Distinct(StringComparer.Ordinal).ToList(),
            references = page.Items,
            totalCount = page.TotalCount,
            nextPageToken = page.NextPageToken,
        };
    }

    [McpServerTool(Name = "find_implementations", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find implementations")]
    [Description("Implementations of an interface or interface member, overrides of a virtual/abstract member, or derived classes of a class.")]
    public async Task<object> FindImplementations(
        [Description(SymbolDescription)] string? symbol = null,
        [Description("File path when addressing by position.")] string? file = null,
        [Description("1-based line.")] int? line = null,
        [Description("1-based column (default 1).")] int? column = null,
        [Description("Include implementations that are themselves abstract/interfaces (default true).")] bool includeAbstract = true,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = await ResolveAsync(session, symbol, file, line, column, cancellationToken).ConfigureAwait(false);
        var results = new List<ISymbol>();
        string relation;

        switch (target)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface } iface:
                relation = "implements";
                results.AddRange(await SymbolFinder.FindImplementationsAsync(iface, session.Solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false));
                results.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(iface, session.Solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
            case INamedTypeSymbol { TypeKind: TypeKind.Class } cls:
                relation = "derives from";
                results.AddRange(await SymbolFinder.FindDerivedClassesAsync(cls, session.Solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol when target.ContainingType.TypeKind == TypeKind.Interface:
                relation = "implements";
                results.AddRange(await SymbolFinder.FindImplementationsAsync(target, session.Solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol when target.IsVirtual || target.IsAbstract || target.IsOverride:
                relation = "overrides";
                results.AddRange(await SymbolFinder.FindOverridesAsync(target, session.Solution, cancellationToken: cancellationToken).ConfigureAwait(false));
                break;
            default:
                throw new ToolException($"'{Symbols.FullName(target)}' is a {Symbols.Kind(target)} that cannot have implementations (expected interface, class, interface member or virtual/abstract member).");
        }

        var implementations = results
            .Where(s => includeAbstract || !(s.IsAbstract || s is INamedTypeSymbol { TypeKind: TypeKind.Interface }))
            .GroupBy(Symbols.FullName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(Symbols.FullName, StringComparer.Ordinal)
            .Select(s => Symbols.Summarize(s, session.Solution, includeDocs: false))
            .ToList();

        return new { symbol = Symbols.FullName(target), kind = Symbols.Kind(target), relation, count = implementations.Count, implementations };
    }

    [McpServerTool(Name = "find_callers", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Find callers")]
    [Description("Methods, properties and constructors that call the given method/property/constructor (direct callers only; use get_call_graph for depth).")]
    public async Task<object> FindCallers(
        [Description(SymbolDescription)] string? symbol = null,
        [Description("File path when addressing by position.")] string? file = null,
        [Description("1-based line.")] int? line = null,
        [Description("1-based column (default 1).")] int? column = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = await ResolveAsync(session, symbol, file, line, column, cancellationToken).ConfigureAwait(false);
        var callers = await CallersAsync(target, session.Solution, cancellationToken).ConfigureAwait(false);
        return new
        {
            symbol = Symbols.FullName(target),
            kind = Symbols.Kind(target),
            count = callers.Count,
            callers = callers.Select(c => new
            {
                caller = Symbols.FullName(c.Caller),
                kind = Symbols.Kind(c.Caller),
                callSites = c.Sites,
            }),
        };
    }

    [McpServerTool(Name = "get_call_graph", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Call graph")]
    [Description("Call graph around a method: callers (who calls it) and callees (what it calls), expanded transitively up to a depth (max 3). Callees are resolved from invocation and object-creation expressions in source.")]
    public async Task<object> GetCallGraph(
        [Description(SymbolDescription)] string? symbol = null,
        [Description("File path when addressing by position.")] string? file = null,
        [Description("1-based line.")] int? line = null,
        [Description("1-based column (default 1).")] int? column = null,
        [Description("Direction: both | callers | callees (default both).")] string? direction = null,
        [Description("Depth 1-3 (default 2).")] int? depth = null,
        [Description("Only include callees declared in source (skip framework calls; default true).")] bool sourceOnly = true,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var dir = (direction ?? "both").ToLowerInvariant();
        ToolGuard.OneOf(dir, "direction", "both", "callers", "callees");
        var maxDepth = Math.Clamp(depth ?? 2, 1, 3);
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = await ResolveAsync(session, symbol, file, line, column, cancellationToken).ConfigureAwait(false);
        if (target is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            throw new ToolException($"'{Symbols.FullName(target)}' is a {Symbols.Kind(target)}; get_call_graph needs a method, property or constructor.");
        }

        var edges = new List<object>();
        var nodes = new Dictionary<string, object>(StringComparer.Ordinal);
        var budget = options.MaxResults;
        AddNode(target, 0);

        if (dir is "both" or "callers")
        {
            var frontier = new List<ISymbol> { target };
            for (var level = 1; level <= maxDepth && frontier.Count > 0 && edges.Count < budget; level++)
            {
                var next = new List<ISymbol>();
                foreach (var node in frontier)
                {
                    foreach (var caller in await CallersAsync(node, session.Solution, cancellationToken).ConfigureAwait(false))
                    {
                        var key = Symbols.FullName(caller.Caller);
                        var isNew = !nodes.ContainsKey(key);
                        AddNode(caller.Caller, -level);
                        edges.Add(new { from = key, to = Symbols.FullName(node), callSites = caller.Sites.Count, kind = "calls" });
                        if (isNew)
                        {
                            next.Add(caller.Caller);
                        }
                    }
                }

                frontier = next;
            }
        }

        if (dir is "both" or "callees")
        {
            var frontier = new List<ISymbol> { target };
            for (var level = 1; level <= maxDepth && frontier.Count > 0 && edges.Count < budget; level++)
            {
                var next = new List<ISymbol>();
                foreach (var node in frontier)
                {
                    foreach (var callee in await CalleesAsync(node, session.Solution, sourceOnly, cancellationToken).ConfigureAwait(false))
                    {
                        var key = Symbols.FullName(callee.Callee);
                        var isNew = !nodes.ContainsKey(key);
                        AddNode(callee.Callee, level);
                        edges.Add(new { from = Symbols.FullName(node), to = key, callSites = callee.Count, kind = "calls" });
                        if (isNew && Symbols.IsSourceSymbol(callee.Callee))
                        {
                            next.Add(callee.Callee);
                        }
                    }
                }

                frontier = next;
            }
        }

        return new { root = Symbols.FullName(target), direction = dir, depth = maxDepth, nodes = nodes.Values, edges, truncated = edges.Count >= budget };

        void AddNode(ISymbol s, int level)
        {
            var key = Symbols.FullName(s);
            if (nodes.ContainsKey(key))
            {
                return;
            }

            var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
            nodes[key] = new
            {
                name = key,
                kind = Symbols.Kind(s),
                level,
                fromMetadata = loc is null,
                location = loc is null ? null : Symbols.Location(loc, session.Solution),
            };
        }
    }

    [McpServerTool(Name = "get_type_hierarchy", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Type hierarchy")]
    [Description("Base type chain, implemented interfaces, and derived/implementing types of a class or interface.")]
    public async Task<object> GetTypeHierarchy(
        [Description("Type name.")] string type,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var symbol = await Symbols.ResolveByNameAsync(session.Solution, type, "type", cancellationToken).ConfigureAwait(false);
        if (symbol is not INamedTypeSymbol named)
        {
            throw new ToolException($"'{type}' is a {Symbols.Kind(symbol)}, not a type.");
        }

        var bases = new List<object>();
        for (var b = named.BaseType; b is not null; b = b.BaseType)
        {
            bases.Add(new { name = b.ToDisplayString(Symbols.FullNameFormat), fromMetadata = !Symbols.IsSourceSymbol(b), isAbstract = b.IsAbstract });
        }

        IEnumerable<INamedTypeSymbol> derived = named.TypeKind == TypeKind.Interface
            ? (await SymbolFinder.FindImplementationsAsync(named, session.Solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Concat(await SymbolFinder.FindDerivedInterfacesAsync(named, session.Solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false))
            : await SymbolFinder.FindDerivedClassesAsync(named, session.Solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new
        {
            type = Symbols.Summarize(named, session.Solution, includeDocs: false),
            baseTypes = bases,
            interfaces = named.AllInterfaces.Select(i => new { name = i.ToDisplayString(Symbols.FullNameFormat), direct = named.Interfaces.Contains(i, SymbolEqualityComparer.Default), fromMetadata = !Symbols.IsSourceSymbol(i) }).ToList(),
            derived = derived.GroupBy(Symbols.FullName, StringComparer.Ordinal).Select(g => g.First()).OrderBy(Symbols.FullName, StringComparer.Ordinal).Select(d => Symbols.Summarize(d, session.Solution, includeDocs: false)).ToList(),
        };
    }

    [McpServerTool(Name = "get_symbols_in_scope", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Symbols in scope")]
    [Description("Locals, parameters, members and types visible at a position in a file (what IntelliSense would offer), optionally filtered by name prefix.")]
    public async Task<object> GetSymbolsInScope(
        [Description("File path.")] string file,
        [Description("1-based line.")] int line,
        [Description("1-based column (default 1).")] int? column = null,
        [Description("Name prefix filter.")] string? prefix = null,
        [Description("Include namespaces and types from referenced assemblies (default false; only source-declared types and members are listed).")] bool includeMetadata = false,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var document = Symbols.GetDocument(session.Solution, file);
        var position = await Symbols.PositionAsync(document, line, column ?? 1, cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) ?? throw new ToolException("No semantic model for this document.");
        var visible = model.LookupSymbols(position);

        var items = visible
            .Where(s => !s.IsImplicitlyDeclared)
            .Where(s => includeMetadata || s is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or ITypeParameterSymbol || Symbols.IsSourceSymbol(s))
            .Where(s => prefix is null || s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(s => s is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
            .GroupBy(s => s.ToDisplayString(Symbols.SignatureFormat), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => ScopeRank(s))
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Take(options.MaxResults)
            .Select(s => new
            {
                s.Name,
                kind = Symbols.Kind(s),
                signature = s.ToDisplayString(Symbols.SignatureFormat),
                type = s switch
                {
                    ILocalSymbol l => l.Type.ToDisplayString(Symbols.FullNameFormat),
                    IParameterSymbol p => p.Type.ToDisplayString(Symbols.FullNameFormat),
                    IFieldSymbol f => f.Type.ToDisplayString(Symbols.FullNameFormat),
                    IPropertySymbol p => p.Type.ToDisplayString(Symbols.FullNameFormat),
                    _ => null,
                },
                containingType = s.ContainingType?.ToDisplayString(Symbols.FullNameFormat),
            })
            .ToList();

        var enclosing = await Symbols.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var member = root?.FindToken(position).Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        var enclosingMember = member is null ? null : model.GetDeclaredSymbol(member, cancellationToken) ?? enclosing;

        var page = Paging.Page(items, pageToken, pageSize);
        return new
        {
            file = document.FilePath,
            line,
            enclosingMember = enclosingMember is null ? null : Symbols.FullName(enclosingMember),
            symbols = page.Items,
            totalCount = page.TotalCount,
            nextPageToken = page.NextPageToken,
        };

        static int ScopeRank(ISymbol s) => s switch
        {
            ILocalSymbol => 0,
            IParameterSymbol => 1,
            IRangeVariableSymbol => 2,
            IFieldSymbol or IPropertySymbol or IEventSymbol => 3,
            IMethodSymbol => 4,
            ITypeParameterSymbol => 5,
            INamedTypeSymbol => 6,
            _ => 7,
        };
    }

    internal static async Task<ISymbol> ResolveAsync(WorkspaceSession session, string? symbol, string? file, int? line, int? column, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            return await Symbols.ResolveByNameAsync(session.Solution, symbol, null, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(file) || line is null)
        {
            throw new ToolException("Pass either 'symbol' (a name) or 'file' + 'line' (+ 'column').");
        }

        var document = Symbols.GetDocument(session.Solution, file);
        var position = await Symbols.PositionAsync(document, line.Value, column ?? 1, cancellationToken).ConfigureAwait(false);
        var found = await Symbols.SymbolAtAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            throw new ToolException($"No symbol at {Path.GetFileName(document.FilePath)}:{line}:{column ?? 1}. Line content: '{text.Lines[line.Value - 1].ToString().Trim()}'");
        }

        return found;
    }

    private static bool MatchesKind(ISymbol symbol, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return true;
        }

        var wanted = kind.Trim().ToLowerInvariant();
        return wanted switch
        {
            "type" or "types" => symbol is INamedTypeSymbol,
            "member" or "members" => symbol is not INamedTypeSymbol,
            "record" => symbol is INamedTypeSymbol { IsRecord: true },
            _ => Symbols.Kind(symbol).Replace(" ", string.Empty, StringComparison.Ordinal) == wanted.Replace(" ", string.Empty, StringComparison.Ordinal),
        };
    }

    internal sealed record CallerInfo(ISymbol Caller, IReadOnlyList<SourceLocation> Sites);

    internal sealed record CalleeInfo(ISymbol Callee, int Count);

    internal static async Task<IReadOnlyList<CallerInfo>> CallersAsync(ISymbol target, Solution solution, CancellationToken cancellationToken)
    {
        var callers = await SymbolFinder.FindCallersAsync(target, solution, cancellationToken).ConfigureAwait(false);
        return callers
            .Where(c => c.IsDirect)
            .GroupBy(c => Symbols.FullName(c.CallingSymbol), StringComparer.Ordinal)
            .Select(g => new CallerInfo(
                g.First().CallingSymbol,
                g.SelectMany(c => c.Locations).Where(l => l.IsInSource).Select(l => Symbols.Location(l, solution, 1)).OrderBy(l => l.File, StringComparer.Ordinal).ThenBy(l => l.Line).ToList()))
            .OrderBy(c => Symbols.FullName(c.Caller), StringComparer.Ordinal)
            .ToList();
    }

    internal static async Task<IReadOnlyList<CalleeInfo>> CalleesAsync(ISymbol caller, Solution solution, bool sourceOnly, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        foreach (var reference in caller.DeclaringSyntaxReferences)
        {
            var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            var document = solution.GetDocument(node.SyntaxTree);
            var model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                continue;
            }

            foreach (var expression in node.DescendantNodes().Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
            {
                var info = model.GetSymbolInfo(expression, cancellationToken);
                var callee = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (callee is null)
                {
                    continue;
                }

                if (callee is IMethodSymbol { ReducedFrom: { } reduced })
                {
                    callee = reduced;
                }

                callee = callee.OriginalDefinition;
                if (sourceOnly && !Symbols.IsSourceSymbol(callee))
                {
                    continue;
                }

                counts[callee] = counts.GetValueOrDefault(callee) + 1;
            }
        }

        return counts.Select(kv => new CalleeInfo(kv.Key, kv.Value)).OrderBy(c => Symbols.FullName(c.Callee), StringComparer.Ordinal).ToList();
    }
}
