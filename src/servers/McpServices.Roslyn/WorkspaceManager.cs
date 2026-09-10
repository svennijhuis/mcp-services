using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using McpServices.Hosting;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace McpServices.Roslyn;

/// <summary>A loaded solution or project, addressed by <c>workspaceId</c>. Owns its Roslyn workspace.</summary>
public sealed class WorkspaceSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal WorkspaceSession(string id, string path, string kind, Workspace workspace, Solution solution, IReadOnlyList<string> loadDiagnostics)
    {
        Id = id;
        Path = path;
        Kind = kind;
        Workspace = workspace;
        Solution = solution;
        LoadDiagnostics = loadDiagnostics;
        LoadedAt = DateTimeOffset.UtcNow;
        Stamps = new Dictionary<DocumentId, DateTime>();
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not null && File.Exists(document.FilePath))
                {
                    Stamps[document.Id] = File.GetLastWriteTimeUtc(document.FilePath);
                }
            }

            if (project.FilePath is not null && File.Exists(project.FilePath))
            {
                ProjectStamps[project.FilePath] = File.GetLastWriteTimeUtc(project.FilePath);
            }
        }
    }

    public string Id { get; }

    public string Path { get; }

    public string Kind { get; }

    public Workspace Workspace { get; }

    /// <summary>Current snapshot; refreshed from disk before every tool call and replaced when a refactoring is applied.</summary>
    public Solution Solution { get; internal set; }

    public DateTimeOffset LoadedAt { get; }

    public DateTimeOffset LastRefreshedAt { get; internal set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> LoadDiagnostics { get; }

    public bool NeedsReload { get; internal set; }

    public int RefreshedDocuments { get; internal set; }

    internal Dictionary<DocumentId, DateTime> Stamps { get; }

    internal Dictionary<string, DateTime> ProjectStamps { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal SemaphoreSlim Gate => _gate;

    public void Dispose()
    {
        Workspace.Dispose();
        _gate.Dispose();
    }
}

/// <summary>
/// Loads solutions/projects through <see cref="MSBuildWorkspace"/>, keeps them as sessions, and
/// keeps each snapshot in sync with disk (changed files are re-read on every call; added/removed
/// files or edited project files mark the session as needing a reload).
/// </summary>
public sealed class WorkspaceManager(RoslynOptions options, ILogger<WorkspaceManager> logger) : IDisposable
{
    private static readonly string[] WorkspaceExtensions = [".sln", ".slnx", ".csproj"];
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", "packages", "artifacts", "TestResults" };

    private readonly ConcurrentDictionary<string, WorkspaceSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public static string? MsBuildRegistrationError { get; private set; }

    /// <summary>Must run before any MSBuild type is touched; safe to call more than once.</summary>
    public static void RegisterMsBuild()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        try
        {
            var instance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();
            if (instance is null)
            {
                MsBuildRegistrationError = "No .NET SDK found by MSBuildLocator; make sure 'dotnet' is on PATH.";
                return;
            }

            MSBuildLocator.RegisterInstance(instance);
        }
        catch (InvalidOperationException ex)
        {
            MsBuildRegistrationError = ex.Message;
        }
    }

    public IReadOnlyCollection<WorkspaceSession> Sessions => _sessions.Values.ToList();

    public IReadOnlyList<string> FindWorkspaceFiles(string? root, int maxDepth = 6)
    {
        var start = string.IsNullOrWhiteSpace(root) ? options.Roots[0] : Path.GetFullPath(root);
        options.EnsureAllowed(start);
        if (!Directory.Exists(start))
        {
            throw new ToolException($"Directory '{root}' does not exist.");
        }

        var results = new List<string>();
        Walk(start, 0);
        return results.OrderBy(p => WorkspaceExtensions.ToList().IndexOf(Path.GetExtension(p).ToLowerInvariant())).ThenBy(p => p, StringComparer.Ordinal).ToList();

        void Walk(string directory, int depth)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (depth < maxDepth && !SkippedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        Walk(entry, depth + 1);
                    }
                }
                else if (WorkspaceExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                }
            }
        }
    }

    public async Task<WorkspaceSession> LoadAsync(string path, bool reload, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ToolGuard.NotEmpty(path, "path")));
        options.EnsureAllowed(full);
        if (Directory.Exists(full))
        {
            var candidates = FindWorkspaceFiles(full, 2);
            full = candidates.FirstOrDefault(c => !c.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) ?? (candidates.Count > 0 ? candidates[0] : null)
                ?? throw new ToolException($"No .sln, .slnx or .csproj found under '{path}'.");
        }

        if (!File.Exists(full))
        {
            throw new ToolException($"'{path}' does not exist.");
        }

        var id = MakeId(full);
        if (!reload && _sessions.TryGetValue(id, out var existing))
        {
            return existing;
        }

        if (MsBuildRegistrationError is not null)
        {
            throw new ToolException("Cannot load projects: " + MsBuildRegistrationError);
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!reload && _sessions.TryGetValue(id, out existing))
            {
                return existing;
            }

            var diagnostics = new List<string>();
            var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["DesignTimeBuild"] = "true" });
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                lock (diagnostics)
                {
                    diagnostics.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
                }

                logger.LogWarning("Workspace {Path}: {Kind} {Message}", full, e.Diagnostic.Kind, e.Diagnostic.Message);
            });

            var isProject = full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
            Solution solution;
            try
            {
                solution = isProject
                    ? (await workspace.OpenProjectAsync(full, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution
                    : await workspace.OpenSolutionAsync(full, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                workspace.Dispose();
                throw new ToolException($"Failed to load '{full}': {ex.Message}", ex);
            }

            // Projects of other languages (F#, VB) load as empty shells; keep C# only.
            foreach (var project in solution.Projects.Where(p => p.Language != LanguageNames.CSharp).ToList())
            {
                solution = solution.RemoveProject(project.Id);
            }

            var session = new WorkspaceSession(id, full, isProject ? "project" : "solution", workspace, solution, diagnostics);
            if (_sessions.TryRemove(id, out var old))
            {
                old.Dispose();
            }

            _sessions[id] = session;
            logger.LogInformation("Loaded {Kind} {Path} as {Id} ({Projects} projects, {Documents} documents)", session.Kind, full, id, solution.ProjectIds.Count, solution.Projects.Sum(p => p.DocumentIds.Count));
            return session;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public bool Unload(string workspaceId)
    {
        if (_sessions.TryRemove(workspaceId, out var session))
        {
            session.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>Resolves a workspaceId; when omitted, the only loaded session is used.</summary>
    public async Task<WorkspaceSession> GetAsync(string? workspaceId, CancellationToken cancellationToken)
    {
        WorkspaceSession? session;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            var all = _sessions.Values.ToList();
            session = all.Count switch
            {
                1 => all[0],
                0 => await AutoLoadAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new ToolException($"Several workspaces are loaded ({string.Join(", ", all.Select(s => $"{s.Id}={Path.GetFileName(s.Path)}"))}); pass workspaceId."),
            };
        }
        else if (!_sessions.TryGetValue(workspaceId, out session))
        {
            // Accept a path instead of an id for convenience.
            if (File.Exists(workspaceId) || Directory.Exists(workspaceId))
            {
                session = await LoadAsync(workspaceId, reload: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new ToolException($"Unknown workspaceId '{workspaceId}'. Call list_workspaces / load_solution first.");
            }
        }

        await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<WorkspaceSession> AutoLoadAsync(CancellationToken cancellationToken)
    {
        var candidates = FindWorkspaceFiles(null, 3);
        var solutions = candidates.Where(c => !c.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var pick = solutions.Count == 1 ? solutions[0] : candidates.Count == 1 ? candidates[0] : null;
        if (pick is null)
        {
            throw new ToolException(candidates.Count == 0
                ? $"No solution or project found under {options.Roots[0]}; call load_solution with a path."
                : $"Several candidates found ({string.Join(", ", candidates.Take(8).Select(Path.GetFileName))}); call load_solution with the one you want.");
        }

        return await LoadAsync(pick, reload: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads documents whose file changed on disk since the last look; cheap (one stat per document).</summary>
    public async Task RefreshAsync(WorkspaceSession session, CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var solution = session.Solution;
            var refreshed = 0;
            foreach (var project in solution.Projects)
            {
                if (project.FilePath is not null && session.ProjectStamps.TryGetValue(project.FilePath, out var projectStamp) && File.Exists(project.FilePath) && File.GetLastWriteTimeUtc(project.FilePath) != projectStamp)
                {
                    session.NeedsReload = true;
                }

                foreach (var document in project.Documents)
                {
                    if (document.FilePath is null)
                    {
                        continue;
                    }

                    if (!File.Exists(document.FilePath))
                    {
                        if (session.Stamps.Remove(document.Id))
                        {
                            solution = solution.RemoveDocument(document.Id);
                            session.NeedsReload = true;
                            refreshed++;
                        }

                        continue;
                    }

                    var stamp = File.GetLastWriteTimeUtc(document.FilePath);
                    if (!session.Stamps.TryGetValue(document.Id, out var known) || known != stamp)
                    {
                        var text = await ReadTextAsync(document.FilePath, cancellationToken).ConfigureAwait(false);
                        solution = solution.WithDocumentText(document.Id, text, PreservationMode.PreserveIdentity);
                        session.Stamps[document.Id] = stamp;
                        refreshed++;
                    }
                }

                // New .cs files next to existing documents are picked up without a full reload.
                if (project.FilePath is not null)
                {
                    var known = new HashSet<string>(project.Documents.Select(d => d.FilePath ?? string.Empty), StringComparer.OrdinalIgnoreCase);
                    foreach (var file in EnumerateSourceFiles(Path.GetDirectoryName(project.FilePath)!))
                    {
                        if (!known.Contains(file))
                        {
                            var text = await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
                            var added = solution.GetProject(project.Id)!.AddDocument(Path.GetFileName(file), text, filePath: file);
                            solution = added.Project.Solution;
                            session.Stamps[added.Id] = File.GetLastWriteTimeUtc(file);
                            refreshed++;
                        }
                    }
                }
            }

            if (refreshed > 0)
            {
                session.Solution = solution;
                session.RefreshedDocuments += refreshed;
                logger.LogDebug("Refreshed {Count} documents in {Id}", refreshed, session.Id);
            }

            session.LastRefreshedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Writes changed documents of <paramref name="newSolution"/> to disk and adopts it as the session snapshot.</summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(WorkspaceSession session, Solution newSolution, CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var written = new List<string>();
            foreach (var projectChange in newSolution.GetChanges(session.Solution).GetProjectChanges())
            {
                foreach (var documentId in projectChange.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
                {
                    var document = newSolution.GetDocument(documentId)!;
                    if (document.FilePath is null)
                    {
                        continue;
                    }

                    var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(document.FilePath, text.ToString(), text.Encoding ?? new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    session.Stamps[documentId] = File.GetLastWriteTimeUtc(document.FilePath);
                    written.Add(document.FilePath);
                }
            }

            session.Solution = newSolution;
            return written;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        var stack = new Stack<string>();
        stack.Push(directory);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        stack.Push(entry);
                    }
                }
                else if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }
    }

    private static async Task<SourceText> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return SourceText.From(stream, Encoding.UTF8);
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string MakeId(string path)
    {
        var normalized = OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Replace(' ', '-') + "-" + Convert.ToHexStringLower(hash)[..6];
    }

    /// <summary>Ad-hoc workspace for a single snippet, referencing the runtime of this process.</summary>
    public static (AdhocWorkspace Workspace, Document Document) CreateSnippet(string code, string? fileName = null)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Snippet", "Snippet", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Latest))
            .WithMetadataReferences(RuntimeReferences.Value);
        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, fileName ?? "Snippet.cs", SourceText.From(code));
        return (workspace, document);
    }

    public static Lazy<IReadOnlyList<MetadataReference>> RuntimeReferences { get; } = new(() =>
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p) is var name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "mscorlib.dll" or "netstandard.dll" or "Microsoft.CSharp.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    });

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
        _loadGate.Dispose();
    }
}
