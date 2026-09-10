using System.Collections.Concurrent;
using McpServices.Hosting;
using McpServices.Index.Embeddings;
using McpServices.Index.Indexing;
using McpServices.Storage;
using Microsoft.Extensions.Logging;

namespace McpServices.Index;

public sealed record RepositoryStatus(
    string RepoId,
    string Root,
    string Name,
    bool Indexed,
    DateTimeOffset? LastIndexedAt,
    string? HeadSha,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    long? LastDurationMs,
    string? LastError,
    string? IndexingInProgressBy,
    bool BackgroundRefreshRunning,
    Freshness Freshness,
    EmbeddingStatus? Embeddings);

/// <summary>
/// Glue between tools and the indexing pipeline: resolves which repository a call refers to,
/// answers "is the index fresh?", and applies the auto-refresh policy (inline for small deltas,
/// background otherwise) so answers are never silently stale.
/// </summary>
public sealed class IndexCoordinator(
    StoreInitializer initializer,
    IndexRepository repository,
    Indexer indexer,
    FreshnessChecker freshness,
    IndexOptions options,
    ILogger<IndexCoordinator> logger,
    EmbeddingService? embeddings = null)
{
    private const int InlineEmbeddingBudget = 200;

    private readonly ConcurrentDictionary<string, Task<IndexRunResult>> _background = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public IndexOptions Options => options;

    public EmbeddingService? Embeddings => embeddings;

    public Task<IKnowledgeStore> StoreAsync(CancellationToken cancellationToken) => initializer.StoreAsync(cancellationToken);

    /// <summary>Resolves the repository for a tool call: explicit root, single configured root, or the only known repository.</summary>
    public async Task<RepoIdentity> ResolveAsync(string? root, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            var expanded = Environment.ExpandEnvironmentVariables(root);
            if (!Directory.Exists(expanded))
            {
                throw new ToolException($"Directory '{root}' does not exist.");
            }

            var identity = RepoIdentity.FromPath(expanded);
            EnsureAllowed(identity);
            return identity;
        }

        if (options.Roots.Count == 1)
        {
            return RepoIdentity.FromPath(options.Roots[0]);
        }

        var store = await StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var known = await repository.ListRepositoriesAsync(connection, cancellationToken).ConfigureAwait(false);
        var candidates = options.Roots.Count > 0
            ? known.Where(k => options.Roots.Any(r => string.Equals(RepoIdentity.Canonicalize(r), k.Root, StringComparison.Ordinal))).ToList()
            : known;

        return candidates.Count switch
        {
            1 => RepoIdentity.FromPath(candidates[0].Root),
            0 when options.Roots.Count > 0 => throw new ToolException($"Several roots are configured ({string.Join(", ", options.Roots)}); pass 'root' to choose one."),
            0 => throw new ToolException("No repository is indexed yet. Call index_repository with the 'root' of your checkout first."),
            _ => throw new ToolException($"Several repositories are known ({string.Join(", ", candidates.Select(c => c.Root))}); pass 'root' to choose one."),
        };
    }

    public void EnsureAllowed(RepoIdentity identity)
    {
        if (!options.RestrictToRoots || options.Roots.Count == 0)
        {
            return;
        }

        var allowed = options.Roots.Select(RepoIdentity.Canonicalize).Any(r =>
            identity.Root.Equals(r, StringComparison.Ordinal) || identity.Root.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (!allowed)
        {
            throw new ToolException($"'{identity.Root}' is outside the configured roots ({string.Join(", ", options.Roots)}).");
        }
    }

    public async Task<IndexRunResult> IndexAsync(RepoIdentity identity, bool force, IReadOnlyCollection<string>? onlyPaths, CancellationToken cancellationToken)
    {
        await StoreAsync(cancellationToken).ConfigureAwait(false);
        var gate = _gates.GetOrAdd(identity.RepoId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await indexer.RunAsync(identity, force, onlyPaths, cancellationToken).ConfigureAwait(false);
            if (result.Completed && embeddings is not null)
            {
                // Semantic layer is best-effort: a slow or offline model must never fail indexing.
                try
                {
                    await embeddings.EmbedPendingAsync(identity.RepoId, InlineEmbeddingBudget, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Embedding after indexing {Root} failed", identity.Root);
                }
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Checks freshness and, depending on the policy, refreshes inline (small delta) or kicks off a
    /// background run. Returns the freshness the caller should attach to its answer.
    /// </summary>
    public async Task<Freshness> EnsureFreshAsync(RepoIdentity identity, CancellationToken cancellationToken)
    {
        await StoreAsync(cancellationToken).ConfigureAwait(false);
        var current = await freshness.CheckAsync(identity, cancellationToken).ConfigureAwait(false);
        if (current.Stale != true || options.AutoRefresh == AutoRefreshMode.Off)
        {
            return current with { RefreshAction = current.Stale == false ? null : "index_repository" };
        }

        var neverIndexed = current.LastIndexedAt is null;
        var smallDelta = !neverIndexed && current.ChangedCount <= options.InlineMaxFiles;
        if (options.AutoRefresh == AutoRefreshMode.Inline && (smallDelta || neverIndexed))
        {
            // First index of a repo can take a while; still worth doing inline once so the very first query works.
            var result = await IndexAsync(identity, force: false, onlyPaths: smallDelta && current.CheckedWith == "git" ? current.ChangedFiles : null, cancellationToken).ConfigureAwait(false);
            if (!result.Completed)
            {
                return current with { Reason = current.Reason + $"; not refreshed: {result.SkippedReason}", RefreshAction = "retry later" };
            }

            return await freshness.CheckAsync(identity, cancellationToken).ConfigureAwait(false) with { RefreshAction = "refreshed inline" };
        }

        StartBackground(identity);
        return current with { RefreshAction = "refreshing in background; results may be stale until index_status reports fresh" };
    }

    public void StartBackground(RepoIdentity identity)
    {
        _background.GetOrAdd(identity.RepoId, key => Task.Run(async () =>
        {
            try
            {
                return await IndexAsync(identity, force: false, onlyPaths: null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background indexing of {Root} failed", identity.Root);
                throw;
            }
            finally
            {
                _background.TryRemove(identity.RepoId, out _);
            }
        }));
    }

    public bool IsBackgroundRunning(string repoId) => _background.ContainsKey(repoId);

    public async Task<RepositoryStatus> StatusAsync(RepoIdentity identity, CancellationToken cancellationToken)
    {
        var store = await StoreAsync(cancellationToken).ConfigureAwait(false);
        RepositoryRow? row;
        EmbeddingStatus? embeddingStatus = null;
        await using (var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            row = await repository.GetRepositoryAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
            if (embeddings is not null)
            {
                embeddingStatus = await embeddings.StatusAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
            }
        }

        var current = await freshness.CheckAsync(identity, cancellationToken).ConfigureAwait(false);
        var holder = await store.DescribeWriterLockAsync(IndexSchema.WriterLockPrefix + identity.RepoId, cancellationToken).ConfigureAwait(false);
        return new RepositoryStatus(
            identity.RepoId,
            identity.Root,
            identity.Name,
            row?.LastIndexedAt is not null,
            row?.LastIndexedAt,
            row?.HeadSha,
            row?.FileCount ?? 0,
            row?.SymbolCount ?? 0,
            row?.ChunkCount ?? 0,
            row?.LastDurationMs,
            row?.LastError,
            holder,
            IsBackgroundRunning(identity.RepoId),
            current,
            embeddingStatus);
    }
}
