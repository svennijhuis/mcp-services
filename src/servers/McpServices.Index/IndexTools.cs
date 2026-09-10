using System.ComponentModel;
using McpServices.Hosting;
using McpServices.Index.Indexing;
using McpServices.Index.Search;
using McpServices.Storage;
using ModelContextProtocol.Server;

namespace McpServices.Index;

[McpServerToolType]
public sealed class IndexTools(IndexCoordinator coordinator, IndexRepository repository, SearchService search, NotesService notes, RelatedFilesService related)
{
    private const string RootDescription = "Repository root. Optional when the server was started with a single root or only one repository is indexed.";

    [McpServerTool(Name = "index_repository", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Index repository")]
    [Description("Index a checkout incrementally: only new or changed files are processed, deleted files are removed. Safe to call often; returns what changed and the resulting status.")]
    public async Task<object> IndexRepository(
        [Description(RootDescription)] string? root = null,
        [Description("Re-hash and re-extract every file even if size and mtime are unchanged.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var result = await coordinator.IndexAsync(identity, force, null, cancellationToken).ConfigureAwait(false);
        var status = await coordinator.StatusAsync(identity, cancellationToken).ConfigureAwait(false);
        return new { run = result, status };
    }

    [McpServerTool(Name = "reindex", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, Title = "Rebuild index")]
    [Description("Drop and rebuild the index of a repository from scratch. Notes and feedback are kept. Use when verify_index reports problems or after changing the extractor.")]
    public async Task<object> Reindex(
        [Description(RootDescription)] string? root = null,
        CancellationToken cancellationToken = default)
    {
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var result = await coordinator.IndexAsync(identity, force: true, null, cancellationToken).ConfigureAwait(false);
        return new { run = result, status = await coordinator.StatusAsync(identity, cancellationToken).ConfigureAwait(false) };
    }

    [McpServerTool(Name = "index_status", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Index status")]
    [Description("Counts, last run, and whether the index is fresh compared to git HEAD and the working tree (with the list of changed files). Does not modify the index.")]
    public async Task<RepositoryStatus> IndexStatus(
        [Description(RootDescription)] string? root = null,
        CancellationToken cancellationToken = default)
    {
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        return await coordinator.StatusAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "verify_index", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Verify index")]
    [Description("Check index integrity: files whose content no longer matches, rows without content, orphaned content, and (SQLite) full-text index consistency. With repair=true orphans are removed and mismatches re-indexed.")]
    public async Task<object> VerifyIndex(
        [Description(RootDescription)] string? root = null,
        [Description("Fix what can be fixed.")] bool repair = false,
        [Description("Maximum files to re-hash (default 2000).")] int? maxFiles = null,
        CancellationToken cancellationToken = default)
    {
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);

        var files = await repository.LoadFilesAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(maxFiles ?? 2000, 1, 100_000);
        var mismatched = new List<string>();
        var missing = new List<string>();
        var checked_ = 0;
        foreach (var file in files.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (checked_++ >= limit)
            {
                break;
            }

            var full = Path.Combine(identity.Root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                missing.Add(file.Path);
                continue;
            }

            try
            {
                var hash = RepoIdentity.ContentHash(await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false));
                if (hash != file.ContentHash)
                {
                    mismatched.Add(file.Path);
                }
            }
            catch (IOException)
            {
                mismatched.Add(file.Path);
            }
        }

        var dangling = await repository.CountDanglingFilesAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
        var orphans = await repository.CountOrphansAsync(connection, cancellationToken).ConfigureAwait(false);
        string? ftsCheck = null;
        if (store.Kind == StoreKind.Sqlite)
        {
            try
            {
                await connection.ExecuteAsync("INSERT INTO symbols_fts(symbols_fts) VALUES ('integrity-check')", cancellationToken: cancellationToken).ConfigureAwait(false);
                await connection.ExecuteAsync("INSERT INTO chunks_fts(chunks_fts) VALUES ('integrity-check')", cancellationToken: cancellationToken).ConfigureAwait(false);
                ftsCheck = "ok";
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                ftsCheck = "corrupt: " + ex.Message;
            }
        }

        IndexRunResult? repairRun = null;
        var orphansRemoved = 0;
        if (repair)
        {
            if (ftsCheck is not null && ftsCheck != "ok")
            {
                await connection.ExecuteAsync("INSERT INTO symbols_fts(symbols_fts) VALUES ('rebuild')", cancellationToken: cancellationToken).ConfigureAwait(false);
                await connection.ExecuteAsync("INSERT INTO chunks_fts(chunks_fts) VALUES ('rebuild')", cancellationToken: cancellationToken).ConfigureAwait(false);
                await connection.ExecuteAsync("INSERT INTO notes_fts(notes_fts) VALUES ('rebuild')", cancellationToken: cancellationToken).ConfigureAwait(false);
                ftsCheck = "rebuilt";
            }

            foreach (var path in missing)
            {
                await repository.DeleteFileAsync(connection, null, identity.RepoId, path, cancellationToken).ConfigureAwait(false);
            }

            orphansRemoved = await repository.CollectOrphansAsync(connection, cancellationToken).ConfigureAwait(false);
            if (mismatched.Count > 0 || dangling > 0)
            {
                repairRun = await coordinator.IndexAsync(identity, force: dangling > 0, onlyPaths: dangling > 0 ? null : mismatched, cancellationToken).ConfigureAwait(false);
            }
        }

        return new
        {
            repoId = identity.RepoId,
            filesChecked = Math.Min(checked_, limit),
            filesTotal = files.Count,
            truncated = files.Count > limit,
            mismatched = mismatched.Take(100).ToList(),
            mismatchedCount = mismatched.Count,
            missingOnDisk = missing.Take(100).ToList(),
            missingCount = missing.Count,
            filesWithoutContent = dangling,
            orphanedContents = orphans,
            fullTextIndex = ftsCheck ?? "n/a (engine maintains it)",
            healthy = mismatched.Count == 0 && missing.Count == 0 && dangling == 0 && (ftsCheck is null or "ok"),
            repaired = repair ? new { orphansRemoved, run = repairRun } : null,
        };
    }

    [McpServerTool(Name = "search_code", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Search code")]
    [Description("Hybrid search over symbols and file contents (BM25 full-text, exact symbol names, feedback boosts, embeddings when configured), fused with reciprocal rank fusion. Returns a queryId: call mark_useful with the hits that helped so future rankings improve. Refreshes the index first when it is stale and the delta is small.")]
    public async Task<object> SearchCode(
        [Description("Natural language or identifier query, e.g. 'where are orders submitted' or 'OrderService Submit'.")] string query,
        [Description(RootDescription)] string? root = null,
        [Description("Filter by language id (csharp, markdown, typescript, ...).")] string? language = null,
        [Description("Only paths starting with this prefix, e.g. src/Orders.")] string? pathPrefix = null,
        [Description("Only symbols of this kind (class, method, property, heading, ...).")] string? kind = null,
        [Description("Maximum hits (default 20, max 100).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(query, "query");
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var freshness = await coordinator.EnsureFreshAsync(identity, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        var max = Math.Clamp(limit ?? 20, 1, 100);
        var filters = new SearchFilters(language, pathPrefix, kind);

        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var exact = await search.ExactSymbolsAsync(connection, identity.RepoId, query.Trim(), filters, Math.Min(max, 10), cancellationToken).ConfigureAwait(false);
        var symbols = await search.SearchSymbolsAsync(connection, identity.RepoId, query, filters, max * 2, cancellationToken).ConfigureAwait(false);
        var chunks = await search.SearchChunksAsync(connection, identity.RepoId, query, filters, max * 2, cancellationToken).ConfigureAwait(false);
        var vectorHits = coordinator.Embeddings is { } embeddings
            ? await embeddings.SearchAsync(connection, identity.RepoId, query, filters, max * 2, cancellationToken).ConfigureAwait(false)
            : null;
        var (queryId, hits) = await search.FuseAsync(connection, identity.RepoId, query, exact, symbols, chunks, vectorHits, max, cancellationToken).ConfigureAwait(false);

        return new { queryId, repoId = identity.RepoId, total = hits.Count, hits, semantic = vectorHits is { Count: > 0 }, freshness };
    }

    [McpServerTool(Name = "search_symbols", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Search symbols")]
    [Description("Find types, members, functions and headings by (partial) name. Cheaper and more precise than search_code when you know roughly what the thing is called.")]
    public async Task<object> SearchSymbols(
        [Description("Name or name fragment, e.g. 'OrderService', 'Submit', 'IRepository<'.")] string query,
        [Description(RootDescription)] string? root = null,
        [Description("Symbol kind filter (class, interface, method, property, heading, ...).")] string? kind = null,
        [Description("Language filter.")] string? language = null,
        [Description("Path prefix filter.")] string? pathPrefix = null,
        [Description("Maximum results (default 30, max 200).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(query, "query");
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var freshness = await coordinator.EnsureFreshAsync(identity, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        var max = Math.Clamp(limit ?? 30, 1, 200);
        var filters = new SearchFilters(language, pathPrefix, kind);

        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var exact = await search.ExactSymbolsAsync(connection, identity.RepoId, query.Trim(), filters, max, cancellationToken).ConfigureAwait(false);
        var fts = await search.SearchSymbolsAsync(connection, identity.RepoId, query, filters, max, cancellationToken).ConfigureAwait(false);
        var merged = exact.Concat(fts).DistinctBy(s => s.Id).Take(max).ToList();
        return new { repoId = identity.RepoId, total = merged.Count, symbols = merged, freshness };
    }

    [McpServerTool(Name = "get_symbol", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get symbol")]
    [Description("Look up a symbol by exact name or fully qualified name and return its declaration(s) with source text.")]
    public async Task<object> GetSymbol(
        [Description("Simple or fully qualified name, e.g. 'OrderService' or 'Shop.Orders.OrderService.Submit'.")] string name,
        [Description(RootDescription)] string? root = null,
        [Description("Include the source lines of the declaration (default true).")] bool includeSource = true,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(name, "name");
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var freshness = await coordinator.EnsureFreshAsync(identity, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var matches = await SearchService.ByNameAsync(connection, identity.RepoId, name.Trim(), 10, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            throw new ToolException($"No symbol named '{name}' in {identity.Name}. Try search_symbols for partial matches.");
        }

        var results = new List<object>();
        foreach (var match in matches)
        {
            string? source = null;
            if (includeSource)
            {
                source = await ReadLinesAsync(identity.Root, match.Path, match.StartLine, Math.Min(match.EndLine, match.StartLine + 200), cancellationToken).ConfigureAwait(false);
            }

            results.Add(new { match.Path, match.Language, match.Name, match.FullName, match.Kind, match.Container, match.Signature, match.Doc, match.StartLine, match.EndLine, source });
        }

        return new { repoId = identity.RepoId, symbols = results, freshness };
    }

    [McpServerTool(Name = "get_file_outline", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "File outline")]
    [Description("Symbols declared in one file in source order (types, members, headings) with signatures and line ranges; the fastest way to understand a file without reading it.")]
    public async Task<object> GetFileOutline(
        [Description("Path relative to the repository root.")] string path,
        [Description(RootDescription)] string? root = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(path, "path");
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var freshness = await coordinator.EnsureFreshAsync(identity, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var file = await repository.GetFileAsync(connection, identity.RepoId, normalized, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException($"'{normalized}' is not in the index of {identity.Name}. Check the path or run index_repository.");
        var symbols = await SearchService.OutlineAsync(connection, identity.RepoId, normalized, cancellationToken).ConfigureAwait(false);
        return new
        {
            repoId = identity.RepoId,
            path = normalized,
            file.Language,
            file.Size,
            contentHash = file.ContentHash,
            symbols = symbols.Select(s => new { s.Name, s.FullName, s.Kind, s.Container, s.Signature, s.Doc, s.StartLine, s.EndLine }),
            freshness,
        };
    }

    [McpServerTool(Name = "find_related_files", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Related files")]
    [Description("Files that historically change together with the given file (git co-change analysis), plus files sharing symbols. Useful to know what else to touch or test.")]
    public async Task<object> FindRelatedFiles(
        [Description("Path relative to the repository root.")] string path,
        [Description(RootDescription)] string? root = null,
        [Description("Maximum results (default 15).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(path, "path");
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (files, commits, head) = await related.FindAsync(connection, identity, path, Math.Clamp(limit ?? 15, 1, 100), cancellationToken).ConfigureAwait(false);
        return new
        {
            repoId = identity.RepoId,
            path = path.Replace('\\', '/').TrimStart('/'),
            commitsAnalyzed = commits,
            headSha = head,
            related = files,
            note = head is null ? "No git history available; co-change analysis needs a git repository." : null,
        };
    }

    [McpServerTool(Name = "remember", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, Title = "Remember note")]
    [Description("Store a note about this codebase for future sessions (architecture facts, gotchas, where things live). Link it to files so recall can warn when they changed. Secrets are redacted.")]
    public async Task<object> Remember(
        [Description("The note, one or a few sentences.")] string note,
        [Description(RootDescription)] string? root = null,
        [Description("Related file paths (relative to the root).")] string[]? files = null,
        [Description("Tags such as 'architecture', 'testing', 'gotcha'.")] string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(note, "note");
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await repository.EnsureRepositoryAsync(connection, identity, cancellationToken).ConfigureAwait(false);
        var id = await notes.RememberAsync(connection, identity.RepoId, note, files ?? [], tags ?? [], cancellationToken).ConfigureAwait(false);
        return new { id, repoId = identity.RepoId, redacted = SecretRedactor.ContainsSecret(note) };
    }

    [McpServerTool(Name = "recall", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Recall notes")]
    [Description("Retrieve notes stored with remember, ranked by relevance and usefulness. possiblyStale=true means a linked file changed since the note was written.")]
    public async Task<object> Recall(
        [Description("Search text; omit to list the most useful notes.")] string? query = null,
        [Description(RootDescription)] string? root = null,
        [Description("Only notes with this tag.")] string? tag = null,
        [Description("Maximum notes (default 10).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = await notes.RecallAsync(connection, identity.RepoId, query, tag, Math.Clamp(limit ?? 10, 1, 100), cancellationToken).ConfigureAwait(false);
        return new { repoId = identity.RepoId, total = results.Count, notes = results };
    }

    [McpServerTool(Name = "forget", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, Title = "Forget note")]
    [Description("Delete a note by id.")]
    public async Task<object> Forget(
        [Description("Note id from remember/recall.")] long id,
        [Description(RootDescription)] string? root = null,
        CancellationToken cancellationToken = default)
    {
        var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await notes.ForgetAsync(connection, identity.RepoId, id, cancellationToken).ConfigureAwait(false);
        return new { id, deleted };
    }

    [McpServerTool(Name = "mark_useful", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, Title = "Mark useful")]
    [Description("Tell the index which search hits (by hit id or path) or notes (by id) actually helped. Boosts them for similar queries later; the effect decays over time.")]
    public async Task<object> MarkUseful(
        [Description("queryId returned by search_code.")] string? queryId = null,
        [Description("Hit ids or file paths from that search.")] string[]? hits = null,
        [Description("Note ids from recall that were useful.")] long[]? noteIds = null,
        [Description(RootDescription)] string? root = null,
        CancellationToken cancellationToken = default)
    {
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var feedbackRows = 0;
        if (!string.IsNullOrWhiteSpace(queryId))
        {
            var remembered = search.GetQuery(queryId) ?? throw new ToolException($"Unknown or expired queryId '{queryId}'. Query ids live for the current server session.");
            if (hits is { Length: > 0 })
            {
                feedbackRows = await search.RecordFeedbackAsync(connection, remembered, hits, cancellationToken).ConfigureAwait(false);
            }
        }

        var notesMarked = 0;
        if (noteIds is { Length: > 0 })
        {
            var identity = await coordinator.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
            notesMarked = await notes.MarkUsefulAsync(connection, identity.RepoId, noteIds, cancellationToken).ConfigureAwait(false);
        }

        return new { feedbackRecorded = feedbackRows, notesMarked };
    }

    [McpServerTool(Name = "list_repositories", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List repositories")]
    [Description("All repositories known to this index store with their counts and last index time.")]
    public async Task<object> ListRepositories(CancellationToken cancellationToken = default)
    {
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var repos = await repository.ListRepositoriesAsync(connection, cancellationToken).ConfigureAwait(false);
        return new
        {
            store = new { kind = store.Kind.ToString().ToLowerInvariant(), location = store.Location, schemaVersion = store.SchemaVersion },
            repositories = repos.Select(r => new { r.RepoId, r.Root, r.Name, r.LastIndexedAt, r.HeadSha, r.FileCount, r.SymbolCount, r.ChunkCount, r.LastError, exists = Directory.Exists(r.Root) }),
        };
    }

    [McpServerTool(Name = "forget_repository", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, Title = "Forget repository")]
    [Description("Remove a repository (its files, notes and feedback) from the index store, e.g. after deleting the checkout.")]
    public async Task<object> ForgetRepository(
        [Description("Repository root or repoId as shown by list_repositories.")] string rootOrRepoId,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(rootOrRepoId, "rootOrRepoId");
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var repos = await repository.ListRepositoriesAsync(connection, cancellationToken).ConfigureAwait(false);
        var target = repos.FirstOrDefault(r => r.RepoId == rootOrRepoId)
            ?? repos.FirstOrDefault(r => string.Equals(r.Root, RepoIdentity.Canonicalize(rootOrRepoId), StringComparison.Ordinal))
            ?? throw new ToolException($"No repository '{rootOrRepoId}' in this store.");
        await repository.DeleteRepositoryAsync(connection, target.RepoId, cancellationToken).ConfigureAwait(false);
        var orphans = await repository.CollectOrphansAsync(connection, cancellationToken).ConfigureAwait(false);
        return new { removed = target.RepoId, target.Root, orphanedContentsRemoved = orphans };
    }

    private static async Task<string?> ReadLinesAsync(string root, string relativePath, int startLine, int endLine, CancellationToken cancellationToken)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(full, cancellationToken).ConfigureAwait(false);
        var from = Math.Clamp(startLine, 1, Math.Max(1, lines.Length));
        var to = Math.Clamp(endLine, from, lines.Length);
        return string.Join('\n', lines.Skip(from - 1).Take(to - from + 1));
    }
}

[McpServerResourceType]
public sealed class IndexResources(IndexCoordinator coordinator, IndexRepository repository)
{
    [McpServerResource(UriTemplate = "index://{repoId}/status", Name = "status", MimeType = "application/json", Title = "Index status")]
    [Description("Status and freshness of one indexed repository (repoId from list_repositories).")]
    public async Task<string> Status(string repoId, CancellationToken cancellationToken = default)
    {
        var store = await coordinator.StoreAsync(cancellationToken).ConfigureAwait(false);
        RepositoryRow row;
        await using (var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            row = await repository.GetRepositoryAsync(connection, repoId, cancellationToken).ConfigureAwait(false)
                ?? throw new ToolException($"Unknown repository '{repoId}'.");
        }

        var status = await coordinator.StatusAsync(RepoIdentity.FromPath(row.Root), cancellationToken).ConfigureAwait(false);
        return ToolJson.Serialize(status);
    }
}
