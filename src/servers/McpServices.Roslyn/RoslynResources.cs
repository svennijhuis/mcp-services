using System.ComponentModel;
using McpServices.Hosting;
using McpServices.Roslyn.Tools;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace McpServices.Roslyn;

[McpServerResourceType]
public sealed class RoslynResources(WorkspaceManager workspaces, RoslynOptions options)
{
    [McpServerResource(UriTemplate = "roslyn://workspaces", Name = "workspaces", MimeType = "application/json", Title = "Loaded workspaces")]
    [Description("The workspaces currently loaded in this server with their projects.")]
    public string Workspaces() => ToolJson.Serialize(new
    {
        roots = options.Roots,
        msbuild = WorkspaceManager.MsBuildRegistrationError ?? "ok",
        workspaces = workspaces.Sessions.Select(s => new
        {
            workspaceId = s.Id,
            s.Kind,
            s.Path,
            s.NeedsReload,
            projects = s.Solution.Projects.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
        }),
    });

    [McpServerResource(UriTemplate = "roslyn://{workspaceId}/diagnostics", Name = "diagnostics", MimeType = "application/json", Title = "Workspace diagnostics")]
    [Description("Compiler errors and warnings of a loaded workspace (compiler only, no analyzers).")]
    public async Task<string> Diagnostics(string workspaceId, CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var all = new List<Diagnostic>();
        foreach (var project in session.Solution.Projects)
        {
            var diagnostics = await DiagnosticsTools.CollectAsync(project, includeAnalyzers: false, cancellationToken).ConfigureAwait(false);
            all.AddRange(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning && !d.IsSuppressed));
        }

        return ToolJson.Serialize(new
        {
            workspaceId = session.Id,
            errors = all.Count(d => d.Severity == DiagnosticSeverity.Error),
            warnings = all.Count(d => d.Severity == DiagnosticSeverity.Warning),
            diagnostics = all.OrderByDescending(d => d.Severity).ThenBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start).Take(500).Select(d => DiagnosticsTools.Describe(d, session.Solution)),
        });
    }

    [McpServerResource(UriTemplate = "roslyn://{workspaceId}/projects", Name = "projects", MimeType = "application/json", Title = "Workspace projects")]
    [Description("Projects of a loaded workspace with their documents.")]
    public async Task<string> Projects(string workspaceId, CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return ToolJson.Serialize(new
        {
            workspaceId = session.Id,
            projects = session.Solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new
            {
                p.Name,
                p.FilePath,
                p.AssemblyName,
                documents = p.Documents.Select(d => d.FilePath).Where(f => f is not null).OrderBy(f => f, StringComparer.Ordinal),
                references = p.ProjectReferences.Select(r => session.Solution.GetProject(r.ProjectId)?.Name).Where(n => n is not null),
            }),
        });
    }
}

[McpServerPromptType]
public sealed class RoslynPrompts(WorkspaceManager workspaces)
{
    [McpServerPrompt(Name = "explain_symbol", Title = "Explain a symbol")]
    [Description("Builds a prompt that explains a type or member using its signature, documentation, source, references and callers from the loaded workspace.")]
    public async Task<string> ExplainSymbol(
        [Description("Symbol name, e.g. 'MyApp.Services.OrderService' or 'OrderService.Place'.")] string symbol,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var target = await Symbols.ResolveByNameAsync(session.Solution, symbol, null, cancellationToken).ConfigureAwait(false);
        var summary = Symbols.Summarize(target, session.Solution, includeDocs: true);
        var source = await Symbols.SourceAsync(target, 150, cancellationToken).ConfigureAwait(false);
        var callers = target is IMethodSymbol or IPropertySymbol
            ? await NavigationTools.CallersAsync(target, session.Solution, cancellationToken).ConfigureAwait(false)
            : [];

        var lines = new List<string>
        {
            $"Explain the C# {summary.Kind} `{summary.FullName}` from the workspace `{Path.GetFileName(session.Path)}`.",
            string.Empty,
            "Cover: what it is for, how it is used, notable design choices, risks or smells, and how you would test it.",
            string.Empty,
            "## Signature",
            "```csharp",
            summary.Signature,
            "```",
        };

        if (summary.Documentation is not null)
        {
            lines.Add(string.Empty);
            lines.Add("## Documentation");
            lines.Add(summary.Documentation);
        }

        if (summary.Locations.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## Declared in");
            lines.AddRange(summary.Locations.Select(l => $"- {l.File}:{l.Line}" + (l.Project is null ? string.Empty : $" ({l.Project})")));
        }

        if (source is not null)
        {
            lines.Add(string.Empty);
            lines.Add("## Source");
            lines.Add("```csharp");
            lines.Add(source);
            lines.Add("```");
        }

        if (callers.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"## Callers ({callers.Count})");
            lines.AddRange(callers.Take(20).Select(c => $"- {Symbols.FullName(c.Caller)} ({c.Sites.Count} call site(s))"));
        }

        return string.Join('\n', lines);
    }
}
