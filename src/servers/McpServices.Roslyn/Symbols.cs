using System.Globalization;
using System.Xml.Linq;
using McpServices.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace McpServices.Roslyn;

public sealed record SourceLocation(string File, int Line, int Column, int EndLine, int EndColumn, string? Project = null, string? Snippet = null);

public sealed record SymbolSummary(
    string Name,
    string FullName,
    string Kind,
    string? ContainingType,
    string? Namespace,
    string Accessibility,
    string Signature,
    IReadOnlyList<string> Modifiers,
    string? Documentation,
    IReadOnlyList<SourceLocation> Locations,
    string? Project,
    bool FromMetadata);

/// <summary>Formatting and lookup helpers shared by the navigation and analysis tools.</summary>
public static class Symbols
{
    public static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeExplicitInterface | SymbolDisplayMemberOptions.IncludeConstantValue,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword | SymbolDisplayKindOptions.IncludeTypeKeyword,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static readonly SymbolDisplayFormat FullNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string FullName(ISymbol symbol) => symbol.ToDisplayString(FullNameFormat);

    public static string Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        INamedTypeSymbol { IsRecord: true } => "record",
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } => "accessor",
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => "local function",
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum member",
        IFieldSymbol { IsConst: true } => "constant",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        INamespaceSymbol => "namespace",
        IParameterSymbol => "parameter",
        ILocalSymbol => "local",
        ITypeParameterSymbol => "type parameter",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    public static SymbolSummary Summarize(ISymbol symbol, Solution solution, bool includeDocs = true, int snippetLines = 0)
    {
        var modifiers = new List<string>();
        if (symbol.IsStatic && symbol is not INamespaceSymbol)
        {
            modifiers.Add("static");
        }

        if (symbol.IsAbstract && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Interface })
        {
            modifiers.Add("abstract");
        }

        if (symbol.IsVirtual)
        {
            modifiers.Add("virtual");
        }

        if (symbol.IsOverride)
        {
            modifiers.Add("override");
        }

        if (symbol.IsSealed && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Enum or TypeKind.Delegate })
        {
            modifiers.Add("sealed");
        }

        if (symbol is IMethodSymbol { IsAsync: true })
        {
            modifiers.Add("async");
        }

        if (symbol is IMethodSymbol { IsExtensionMethod: true })
        {
            modifiers.Add("extension");
        }

        if (symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true })
        {
            modifiers.Add("readonly");
        }

        var locations = symbol.Locations.Where(l => l.IsInSource).Select(l => Location(l, solution, snippetLines)).ToList();
        var project = locations.Count > 0 ? locations[0].Project : null;
        return new SymbolSummary(
            symbol.Name.Length > 0 ? symbol.Name : symbol.ToDisplayString(),
            FullName(symbol),
            Kind(symbol),
            symbol.ContainingType?.ToDisplayString(FullNameFormat),
            symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            Accessibility(symbol.DeclaredAccessibility),
            symbol.ToDisplayString(SignatureFormat),
            modifiers,
            includeDocs ? Documentation(symbol) : null,
            locations,
            project,
            symbol.Locations.All(l => l.IsInMetadata));
    }

    public static SourceLocation Location(Location location, Solution solution, int snippetLines = 0)
    {
        var span = location.GetLineSpan();
        var document = solution.GetDocument(location.SourceTree);
        string? snippet = null;
        if (snippetLines > 0 && location.SourceTree is not null)
        {
            var text = location.SourceTree.GetText();
            var start = span.StartLinePosition.Line;
            var end = Math.Min(text.Lines.Count - 1, Math.Max(span.EndLinePosition.Line, start + snippetLines - 1));
            snippet = string.Join('\n', Enumerable.Range(start, end - start + 1).Select(i => text.Lines[i].ToString()));
        }

        return new SourceLocation(
            span.Path,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1,
            document?.Project.Name,
            snippet);
    }

    public static SourceLocation Location(ReferenceLocation reference, int snippetLines = 1)
    {
        var span = reference.Location.GetLineSpan();
        string? snippet = null;
        if (snippetLines > 0 && reference.Location.SourceTree is not null)
        {
            var text = reference.Location.SourceTree.GetText();
            snippet = text.Lines[span.StartLinePosition.Line].ToString().Trim();
        }

        return new SourceLocation(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1, reference.Document.Project.Name, snippet);
    }

    public static string? Documentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var element = XElement.Parse(xml.Trim().StartsWith('<') && !xml.Contains("<member", StringComparison.Ordinal) ? $"<doc>{xml}</doc>" : xml);
            var parts = new List<string>();
            var summary = element.Descendants("summary").FirstOrDefault();
            if (summary is not null)
            {
                parts.Add(Clean(summary));
            }

            foreach (var parameter in element.Descendants("param"))
            {
                parts.Add($"{parameter.Attribute("name")?.Value}: {Clean(parameter)}");
            }

            var returns = element.Descendants("returns").FirstOrDefault();
            if (returns is not null)
            {
                parts.Add("Returns: " + Clean(returns));
            }

            var remarks = element.Descendants("remarks").FirstOrDefault();
            if (remarks is not null)
            {
                parts.Add(Clean(remarks));
            }

            return parts.Count == 0 ? null : string.Join('\n', parts.Where(p => p.Length > 0));
        }
        catch (System.Xml.XmlException)
        {
            return xml.Trim();
        }

        static string Clean(XElement element) => string.Join(' ', element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Source text of the primary declaration, with the containing lines.</summary>
    public static async Task<string?> SourceAsync(ISymbol symbol, int maxLines, CancellationToken cancellationToken)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
        {
            return null;
        }

        var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
        var text = node.GetText();
        var lines = text.Lines.Count;
        var body = node.ToFullString().Trim();
        if (lines <= maxLines)
        {
            return body;
        }

        return string.Join('\n', body.Split('\n').Take(maxLines)) + $"\n// … {lines - maxLines} more lines";
    }

    public static Document GetDocument(Solution solution, string file)
    {
        ToolGuard.NotEmpty(file, "file");
        var normalized = file.Replace('\\', '/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.IsPathRooted(file) ? Path.GetFullPath(file) : null;

        var matches = solution.Projects.SelectMany(p => p.Documents).Where(d =>
        {
            if (d.FilePath is null)
            {
                return false;
            }

            if (full is not null)
            {
                return string.Equals(Path.GetFullPath(d.FilePath), full, comparison);
            }

            var candidate = d.FilePath.Replace('\\', '/');
            return candidate.EndsWith("/" + normalized.TrimStart('/'), comparison) || string.Equals(candidate, normalized, comparison);
        }).ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ToolException($"File '{file}' is not part of the workspace (only C# source files that belong to a loaded project can be addressed)."),
            _ when matches.Select(m => m.FilePath).Distinct(StringComparer.Ordinal).Count() == 1 => matches[0], // Same file in multi-targeted projects.
            _ => throw new ToolException($"File '{file}' is ambiguous; use a longer path. Candidates: {string.Join(", ", matches.Select(m => m.FilePath).Distinct().Take(5))}"),
        };
    }

    public static async Task<int> PositionAsync(Document document, int line, int column, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count)
        {
            throw new ToolException($"Line {line} is out of range (1-{text.Lines.Count}).");
        }

        var textLine = text.Lines[line - 1];
        var col = Math.Clamp(column - 1, 0, Math.Max(0, textLine.Span.Length));
        return textLine.Start + col;
    }

    public static async Task<ISymbol?> SymbolAtAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is not null)
        {
            return symbol;
        }

        // FindSymbolAtPosition only covers references; fall back to the declaration under the cursor.
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null)
        {
            return null;
        }

        var token = root.FindToken(position);
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            var declared = model.GetDeclaredSymbol(node, cancellationToken);
            if (declared is not null)
            {
                return declared;
            }

            if (node is ExpressionSyntax or AttributeSyntax)
            {
                var info = model.GetSymbolInfo(node, cancellationToken);
                if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is { } referenced)
                {
                    return referenced;
                }
            }

            if (node is StatementSyntax or MemberDeclarationSyntax)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a symbol by name: <c>Namespace.Type</c>, <c>Type</c>, <c>Type.Member</c> or a bare member name.
    /// Ambiguity is reported with candidates instead of guessed.
    /// </summary>
    public static async Task<ISymbol> ResolveByNameAsync(Solution solution, string name, string? kind, CancellationToken cancellationToken)
    {
        ToolGuard.NotEmpty(name, "symbol");
        var query = name.Trim();
        var paren = query.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0)
        {
            query = query[..paren];
        }

        // Fully qualified type via metadata name first (fast and exact).
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var type = compilation?.GetTypeByMetadataName(ToMetadataName(query));
            if (type is not null && type.Locations.Any(l => l.IsInSource))
            {
                return type;
            }
        }

        var lastDot = query.LastIndexOf('.');
        var simple = lastDot >= 0 ? query[(lastDot + 1)..] : query;
        var container = lastDot >= 0 ? query[..lastDot] : null;
        var generic = simple.IndexOf('<', StringComparison.Ordinal);
        if (generic > 0)
        {
            simple = simple[..generic];
        }

        var declarations = await SymbolFinder.FindSourceDeclarationsAsync(solution, simple, ignoreCase: false, cancellationToken).ConfigureAwait(false);
        var candidates = declarations
            .Where(s => s is not INamespaceSymbol)
            .Where(s => container is null || FullName(s).StartsWith(container + ".", StringComparison.Ordinal) || FullName(s).Contains("." + container + ".", StringComparison.Ordinal) || (s.ContainingType?.Name == container))
            .Where(s => kind is null || Kind(s).Equals(kind, StringComparison.OrdinalIgnoreCase) || (kind.Equals("type", StringComparison.OrdinalIgnoreCase) && s is INamedTypeSymbol))
            .GroupBy(FullName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (candidates.Count == 0)
        {
            var loose = await SymbolFinder.FindSourceDeclarationsAsync(solution, simple, ignoreCase: true, cancellationToken).ConfigureAwait(false);
            candidates = loose.Where(s => s is not INamespaceSymbol).GroupBy(FullName, StringComparer.Ordinal).Select(g => g.First()).ToList();
        }

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new ToolException($"No symbol named '{name}' found in the workspace. Try find_symbols with matchMode 'contains'."),
            _ when candidates.Count(c => c is INamedTypeSymbol) == 1 && container is null && kind is null => candidates.First(c => c is INamedTypeSymbol),
            _ => throw new ToolException($"'{name}' is ambiguous ({candidates.Count} matches). Use the full name, e.g.: {string.Join(", ", candidates.Take(8).Select(FullName))}"),
        };
    }

    private static string ToMetadataName(string name)
    {
        var generic = name.IndexOf('<', StringComparison.Ordinal);
        if (generic < 0)
        {
            return name;
        }

        var arity = name[generic..].Count(c => c == ',') + 1;
        return name[..generic] + "`" + arity.ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsSourceSymbol(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    public static string Accessibility(Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
        Microsoft.CodeAnalysis.Accessibility.NotApplicable => string.Empty,
        var other => other.ToString().ToLowerInvariant(),
    };
}
