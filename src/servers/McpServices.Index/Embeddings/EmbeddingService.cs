using System.Collections.Concurrent;
using System.Data.Common;
using McpServices.Index.Search;
using McpServices.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace McpServices.Index.Embeddings;

public sealed record EmbeddingStatus(string Model, int Embedded, int Total, int Pending, string? LastError);

/// <summary>
/// Optional semantic layer. Chunks are embedded lazily after indexing (only content never embedded
/// with the current model), stored as float32 blobs, and searched in-process by cosine similarity
/// against a per-repository cache. Switching models simply starts filling in the new fingerprint.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    private const int BatchSize = 32;
    private const int MaxChunkChars = 4000;

    private readonly IKnowledgeStore _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly ConcurrentDictionary<string, VectorCache> _caches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _background = new(StringComparer.Ordinal);
    private string? _lastError;

    public EmbeddingService(IKnowledgeStore store, IEmbeddingGenerator<string, Embedding<float>> generator, string fingerprint, ILogger<EmbeddingService> logger)
    {
        _store = store;
        _generator = generator;
        _logger = logger;
        Fingerprint = fingerprint;
    }

    public string Fingerprint { get; }

    /// <summary>Embeds up to <paramref name="budget"/> pending chunks now; the remainder continues in the background.</summary>
    public async Task<int> EmbedPendingAsync(string repoId, int budget, CancellationToken cancellationToken)
    {
        var done = await EmbedBatchesAsync(repoId, budget, cancellationToken).ConfigureAwait(false);
        if (done >= budget)
        {
            _ = _background.GetOrAdd(repoId, key => Task.Run(async () =>
            {
                try
                {
                    await EmbedBatchesAsync(key, int.MaxValue, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _lastError = ex.Message;
                    _logger.LogWarning(ex, "Background embedding for {RepoId} stopped", key);
                }
                finally
                {
                    _background.TryRemove(key, out _);
                }
            }));
        }

        return done;
    }

    public async Task<EmbeddingStatus> StatusAsync(DbConnection connection, string repoId, CancellationToken cancellationToken)
    {
        var total = await connection.ScalarAsync<int>("SELECT COUNT(*) FROM chunks c WHERE c.content_hash IN (SELECT content_hash FROM files WHERE repo_id = @repoId)", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var embedded = await connection.ScalarAsync<int>("SELECT COUNT(*) FROM embeddings e WHERE e.model = @model AND e.content_hash IN (SELECT content_hash FROM files WHERE repo_id = @repoId)", new { repoId, model = Fingerprint }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new EmbeddingStatus(Fingerprint, embedded, total, Math.Max(0, total - embedded), _lastError);
    }

    public async Task<List<(string Key, SearchHit Hit)>> SearchAsync(DbConnection connection, string repoId, string query, SearchFilters filters, int limit, CancellationToken cancellationToken)
    {
        float[] queryVector;
        try
        {
            var generated = await _generator.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);
            queryVector = VectorCodec.Normalize(generated[0].Vector.Span);
        }
        catch (Exception ex) when (ex is EmbeddingException or HttpRequestException or TaskCanceledException)
        {
            _lastError = ex.Message;
            _logger.LogWarning("Embedding the query failed; falling back to keyword search: {Message}", ex.Message);
            return [];
        }

        var cache = await LoadCacheAsync(connection, repoId, cancellationToken).ConfigureAwait(false);
        var scored = new List<(float Score, CachedVector Entry)>();
        foreach (var entry in cache.Entries)
        {
            if (!string.IsNullOrEmpty(filters.Language) && !entry.Language.Equals(filters.Language, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(filters.PathPrefix) && !entry.Path.StartsWith(filters.PathPrefix.Replace('\\', '/').TrimStart('/'), StringComparison.Ordinal))
            {
                continue;
            }

            var score = VectorCodec.Cosine(queryVector, entry.Vector);
            if (score > 0.2f)
            {
                scored.Add((score, entry));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .Select(s => ($"chunk:{s.Entry.Path}:{s.Entry.StartLine}", SearchService.FromChunk(new ChunkHit(0, s.Entry.Path, s.Entry.Language, s.Entry.StartLine, s.Entry.EndLine, s.Entry.Heading, s.Entry.Snippet))))
            .ToList();
    }

    private async Task<int> EmbedBatchesAsync(string repoId, int budget, CancellationToken cancellationToken)
    {
        var done = 0;
        while (done < budget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<(string Hash, int Ordinal, string Text)> pending;
            await using (var connection = await _store.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                pending = await connection.QueryAsync(
                    """
                    SELECT c.content_hash, c.ordinal, c.text FROM chunks c
                    WHERE c.content_hash IN (SELECT content_hash FROM files WHERE repo_id = @repoId)
                      AND NOT EXISTS (SELECT 1 FROM embeddings e WHERE e.content_hash = c.content_hash AND e.ordinal = c.ordinal AND e.model = @model)
                    ORDER BY c.id LIMIT @limit
                    """,
                    r => (r.GetString("content_hash"), r.GetInt32("ordinal"), r.GetString("text")),
                    new { repoId, model = Fingerprint, limit = Math.Min(BatchSize, budget - done) },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (pending.Count == 0)
            {
                break;
            }

            GeneratedEmbeddings<Embedding<float>> vectors;
            try
            {
                vectors = await _generator.GenerateAsync(pending.Select(p => p.Text.Length > MaxChunkChars ? p.Text[..MaxChunkChars] : p.Text), cancellationToken: cancellationToken).ConfigureAwait(false);
                _lastError = null;
            }
            catch (Exception ex) when (ex is EmbeddingException or HttpRequestException or TaskCanceledException)
            {
                _lastError = ex.Message;
                _logger.LogWarning("Embedding batch failed, will retry on the next run: {Message}", ex.Message);
                break;
            }

            await using (var connection = await _store.OpenAsync(cancellationToken).ConfigureAwait(false))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var i = 0; i < pending.Count && i < vectors.Count; i++)
                {
                    await connection.ExecuteAsync(
                        $"INSERT INTO embeddings (content_hash, ordinal, model, vector) VALUES (@hash, @ordinal, @model, @vector) {_store.Dialect.Upsert("content_hash, ordinal, model", "vector")}",
                        new { hash = pending[i].Hash, ordinal = pending[i].Ordinal, model = Fingerprint, vector = VectorCodec.Normalize(vectors[i].Vector.Span) },
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            done += pending.Count;
            _caches.TryRemove(repoId, out _);
        }

        return done;
    }

    private async Task<VectorCache> LoadCacheAsync(DbConnection connection, string repoId, CancellationToken cancellationToken)
    {
        var count = await connection.ScalarAsync<int>("SELECT COUNT(*) FROM embeddings e WHERE e.model = @model AND e.content_hash IN (SELECT content_hash FROM files WHERE repo_id = @repoId)", new { repoId, model = Fingerprint }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_caches.TryGetValue(repoId, out var cached) && cached.Count == count)
        {
            return cached;
        }

        var entries = await connection.QueryAsync(
            """
            SELECT f.path, f.language, c.start_line, c.end_line, c.heading, c.text, e.vector
            FROM embeddings e
            JOIN files f ON f.content_hash = e.content_hash AND f.repo_id = @repoId
            JOIN chunks c ON c.content_hash = e.content_hash AND c.ordinal = e.ordinal
            WHERE e.model = @model
            """,
            r => new CachedVector(r.GetString("path"), r.GetString("language"), r.GetInt32("start_line"), r.GetInt32("end_line"), r.GetStringOrNull("heading"), Snippet(r.GetString("text")), VectorCodec.FromBytes(r.GetBytesOrNull("vector") ?? [])),
            new { repoId, model = Fingerprint },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var cache = new VectorCache(count, entries);
        _caches[repoId] = cache;
        return cache;
    }

    private static string Snippet(string text) => text.Length <= 400 ? text : text[..400] + " …";

    public void Dispose() => _generator.Dispose();

    private sealed record CachedVector(string Path, string Language, int StartLine, int EndLine, string? Heading, string Snippet, float[] Vector);

    private sealed record VectorCache(int Count, List<CachedVector> Entries);
}
