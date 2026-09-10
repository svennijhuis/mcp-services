using System.ComponentModel;
using McpServices.Hosting;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace McpServices.Roslyn.Tools;

[McpServerToolType]
public sealed class WorkspaceTools(WorkspaceManager workspaces, RoslynOptions options)
{
    internal const string WorkspaceIdDescription = "Workspace id from load_solution/list_workspaces (or a path). Optional when exactly one workspace is loaded; with none loaded the single solution under the root is loaded automatically.";

    [McpServerTool(Name = "list_workspaces", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List workspaces")]
    [Description("Find .sln/.slnx/.csproj files under a root and list the workspaces that are currently loaded.")]
    public object ListWorkspaces(
        [Description("Directory to search (default: the server's first root).")] string? root = null,
        [Description("Maximum directory depth (default 6).")] int? maxDepth = null)
    {
        var found = workspaces.FindWorkspaceFiles(root, Math.Clamp(maxDepth ?? 6, 1, 12));
        return new
        {
            roots = options.Roots,
            candidates = found.Select(f => new { path = f, kind = Path.GetExtension(f).ToLowerInvariant() switch { ".csproj" => "project", _ => "solution" } }),
            loaded = workspaces.Sessions.Select(Describe),
            msbuild = WorkspaceManager.MsBuildRegistrationError ?? "ok",
        };
    }

    [McpServerTool(Name = "load_solution", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Load solution")]
    [Description("Load a .sln/.slnx (or a directory containing one) with MSBuild and return its workspaceId, projects and any load warnings. Already loaded workspaces are returned as-is unless reload=true.")]
    public async Task<object> LoadSolution(
        [Description("Path to the solution file or its directory.")] string path,
        [Description("Reload even if already loaded (after project file changes).")] bool reload = false,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.LoadAsync(path, reload, cancellationToken).ConfigureAwait(false);
        return Describe(session);
    }

    [McpServerTool(Name = "load_project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Load project")]
    [Description("Load a single .csproj (with its project references) and return its workspaceId.")]
    public async Task<object> LoadProject(
        [Description("Path to the .csproj file.")] string path,
        [Description("Reload even if already loaded.")] bool reload = false,
        CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("load_project expects a .csproj path; use load_solution for solutions or directories.");
        }

        var session = await workspaces.LoadAsync(path, reload, cancellationToken).ConfigureAwait(false);
        return Describe(session);
    }

    [McpServerTool(Name = "workspace_status", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Workspace status")]
    [Description("Projects, document counts, load diagnostics and whether the snapshot is in sync with disk (needsReload after project-file or file-set changes).")]
    public async Task<object> WorkspaceStatus(
        [Description(WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return Describe(session);
    }

    [McpServerTool(Name = "unload_workspace", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Unload workspace")]
    [Description("Release a loaded workspace and its compilations.")]
    public object UnloadWorkspace([Description("Workspace id.")] string workspaceId) =>
        new { unloaded = workspaces.Unload(ToolGuard.NotEmpty(workspaceId, "workspaceId")) };

    [McpServerTool(Name = "list_projects", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List projects")]
    [Description("Projects in the workspace with target framework, output kind, document counts and project references.")]
    public async Task<object> ListProjects(
        [Description(WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return new
        {
            workspaceId = session.Id,
            projects = session.Solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => ProjectInfo(p, session.Solution)),
        };
    }

    [McpServerTool(Name = "get_project_info", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Project info")]
    [Description("Details of one project: paths, assembly name, framework, language version, nullable setting, references, analyzers and documents.")]
    public async Task<object> GetProjectInfo(
        [Description("Project name (as in list_projects).")] string project,
        [Description(WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var p = FindProject(session.Solution, project);
        var info = ProjectInfo(p, session.Solution);
        return new
        {
            info.Name,
            info.FilePath,
            info.AssemblyName,
            info.TargetFramework,
            info.OutputKind,
            languageVersion = (p.ParseOptions as Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)?.LanguageVersion.ToString(),
            nullable = (p.CompilationOptions as Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions)?.NullableContextOptions.ToString(),
            defaultNamespace = p.DefaultNamespace,
            info.DocumentCount,
            info.ProjectReferences,
            metadataReferences = p.MetadataReferences.Count,
            analyzers = p.AnalyzerReferences.Select(a => a.Display).Where(d => d is not null).OrderBy(d => d, StringComparer.Ordinal),
            documents = p.Documents.Select(d => Relative(d.FilePath, p.FilePath)).OrderBy(d => d, StringComparer.Ordinal),
        };
    }

    [McpServerTool(Name = "list_source_files", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List source files")]
    [Description("Source files in the workspace (optionally one project), paginated.")]
    public async Task<object> ListSourceFiles(
        [Description(WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Project name filter.")] string? project = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var projects = project is null ? session.Solution.Projects : [FindProject(session.Solution, project)];
        var files = projects.SelectMany(p => p.Documents.Select(d => new { project = p.Name, path = d.FilePath ?? d.Name })).OrderBy(f => f.path, StringComparer.Ordinal).ToList();
        return Paging.Page(files, pageToken, pageSize);
    }

    [McpServerTool(Name = "list_namespaces", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List namespaces")]
    [Description("Namespaces declared in source with the number of types in each.")]
    public async Task<object> ListNamespaces(
        [Description(WorkspaceIdDescription)] string? workspaceId = null,
        [Description("Project name filter.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var projects = project is null ? session.Solution.Projects : [FindProject(session.Solution, project)];
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in projects)
        {
            var compilation = await p.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                var ns = type.ContainingNamespace.IsGlobalNamespace ? "<global>" : type.ContainingNamespace.ToDisplayString();
                counts[ns] = counts.GetValueOrDefault(ns) + 1;
            }
        }

        return new { workspaceId = session.Id, namespaces = counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new { name = c.Key, types = c.Value }) };
    }

    internal static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.Locations.Any(l => l.IsInSource))
            {
                yield return type;
                foreach (var nested in Nested(type))
                {
                    yield return nested;
                }
            }
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in SourceTypes(child))
            {
                yield return type;
            }
        }

        static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in Nested(nested))
                {
                    yield return deeper;
                }
            }
        }
    }

    internal static Project FindProject(Solution solution, string name)
    {
        ToolGuard.NotEmpty(name, "project");
        var matches = solution.Projects.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.AssemblyName.Equals(name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.FilePath, Path.GetFullPath(name), StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ToolException($"Project '{name}' not found. Known: {string.Join(", ", solution.Projects.Select(p => p.Name))}"),
            _ => matches.FirstOrDefault(m => m.Name.Equals(name, StringComparison.Ordinal)) ?? matches[0],
        };
    }

    private static object Describe(WorkspaceSession session) => new
    {
        workspaceId = session.Id,
        session.Kind,
        session.Path,
        loadedAt = session.LoadedAt,
        lastRefreshedAt = session.LastRefreshedAt,
        session.NeedsReload,
        refreshedDocuments = session.RefreshedDocuments,
        projects = session.Solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new { p.Name, targetFramework = TargetFramework(p), documents = p.DocumentIds.Count }),
        loadDiagnostics = session.LoadDiagnostics.Take(25),
        loadDiagnosticCount = session.LoadDiagnostics.Count,
    };

    private static ProjectDto ProjectInfo(Project p, Solution solution) => new(
        p.Name,
        p.FilePath,
        p.AssemblyName,
        TargetFramework(p),
        p.CompilationOptions?.OutputKind.ToString(),
        p.DocumentIds.Count,
        p.ProjectReferences.Select(r => solution.GetProject(r.ProjectId)?.Name ?? r.ProjectId.ToString()).OrderBy(n => n, StringComparer.Ordinal).ToList());

    private static string? TargetFramework(Project project)
    {
        // Roslyn names multi-targeted projects "Name(net10.0)"; single-target projects expose it only via the output path.
        var open = project.Name.IndexOf('(', StringComparison.Ordinal);
        if (open > 0 && project.Name.EndsWith(')'))
        {
            return project.Name[(open + 1)..^1];
        }

        var output = project.OutputFilePath;
        if (output is null)
        {
            return null;
        }

        var parts = output.Replace('\\', '/').Split('/');
        return parts.Length >= 2 ? parts[^2] : null;
    }

    internal static string Relative(string? path, string? basePath)
    {
        if (path is null)
        {
            return string.Empty;
        }

        var baseDir = basePath is null ? null : Path.GetDirectoryName(basePath);
        return baseDir is null ? path : Path.GetRelativePath(baseDir, path).Replace('\\', '/');
    }

    private sealed record ProjectDto(string Name, string? FilePath, string AssemblyName, string? TargetFramework, string? OutputKind, int DocumentCount, IReadOnlyList<string> ProjectReferences);
}
