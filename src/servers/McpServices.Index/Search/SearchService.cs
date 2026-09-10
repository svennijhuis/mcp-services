using System.Collections.Concurrent;
using System.Data.Common;
using McpServices.Index.Indexing;
using McpServices.Storage;

namespace McpServices.Index.Search;

public sealed record SymbolHit(long Id, string Path, string Language, string Name, string FullName, string Kind, string? Container, string Signature, string? Doc, int StartLine, int EndLine);

public sealed record ChunkHit(long Id, string Path, string Language, int StartLine, int EndLine, string? Heading, string Snippet);

public sealed record SearchHit(string Id, string Type, string Path, string Language, int StartLine, int EndLine, string Title, string? Detail, string Snippet, double Score, IReadOnlyDictionary<string, int> Ranks);

public sealed record SearchFilters(string? Language, string? PathPrefix, string? Kind);

public sealed record RememberedQuery(string RepoId, string QueryTokens, IReadOnlyList<SearchHit> Hits, DateTimeOffset At);

/// <summary>
/// Hybrid search: FTS over symbols and chunks, an exact-name ranking, optional vector ranking
/// (supplied by the caller) and feedback boosts, fused with RRF. Every query gets an id so agents
/// can report which hits helped (<c>mark_useful</c>) and improve later rankings.
/// </summary>
public sealed class SearchService(IKnowledgeStore store)
{
    private const int MaxRemembered = 200;
    private static readonly TimeSpan FeedbackHalfLife = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, RememberedQuery> _queries = new(StringComparer.Ordinal);

    public async Task<List<SymbolHit>> SearchSymbolsAsync(DbConnection connection, string repoId, string query, SearchFilters filters, int limit, CancellationToken cancellationToken)
    {
        var fts = store.Dialect.FullTextQuery(query);
        if (string.IsNullOrEmpty(fts) || fts == "\"\"")
        {
            return [];
        }

        var (where, parameters) = Filters(filters, "f", "s");
        parameters["repoId"] = repoId;
        parameters["q"] = fts;
        parameters["limit"] = limit;

        var sql = store.Kind == StoreKind.Sqlite
            ? $"""
                SELECT s.id, f.path, f.language, s.name, s.full_name, s.kind, s.container, s.signature, s.doc, s.start_line, s.end_line
                FROM symbols_fts JOIN symbols s ON s.id = symbols_fts.rowid JOIN files f ON f.content_hash = s.content_hash
                WHERE symbols_fts MATCH @q AND f.repo_id = @repoId {where}
                ORDER BY bm25(symbols_fts, 10.0, 4.0, 1.0, 1.0, 6.0), s.name LIMIT @limit
                """
            : $"""
                SELECT s.id, f.path, f.language, s.name, s.full_name, s.kind, s.container, s.signature, s.doc, s.start_line, s.end_line
                FROM symbols s JOIN files f ON f.content_hash = s.content_hash
                WHERE s.tsv @@ to_tsquery('english', @q) AND f.repo_id = @repoId {where}
                ORDER BY ts_rank_cd(s.tsv, to_tsquery('english', @q)) DESC, s.name LIMIT @limit
                """;

        return await connection.QueryAsync(sql, MapSymbol, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Symbols whose simple name equals or starts with the query, independent of FTS tokenisation.</summary>
    public async Task<List<SymbolHit>> ExactSymbolsAsync(DbConnection connection, string repoId, string name, SearchFilters filters, int limit, CancellationToken cancellationToken)
    {
        var (where, parameters) = Filters(filters, "f", "s");
        parameters["repoId"] = repoId;
        parameters["exact"] = name;
        parameters["prefix"] = name + "%";
        parameters["limit"] = limit;
        var like = store.Dialect.ILike("s.name", "@prefix");
        var sql = $"""
            SELECT s.id, f.path, f.language, s.name, s.full_name, s.kind, s.container, s.signature, s.doc, s.start_line, s.end_line
            FROM symbols s JOIN files f ON f.content_hash = s.content_hash
            WHERE f.repo_id = @repoId AND ({like} OR {store.Dialect.ILike("s.full_name", "@prefix")}) {where}
            ORDER BY CASE WHEN s.name = @exact THEN 0 WHEN {like} THEN 1 ELSE 2 END, LENGTH(s.name), s.full_name LIMIT @limit
            """;
        return await connection.QueryAsync(sql, MapSymbol, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ChunkHit>> SearchChunksAsync(DbConnection connection, string repoId, string query, SearchFilters filters, int limit, CancellationToken cancellationToken)
    {
        var fts = store.Dialect.FullTextQuery(query);
        if (string.IsNullOrEmpty(fts) || fts == "\"\"")
        {
            return [];
        }

        var (where, parameters) = Filters(filters with { Kind = null }, "f", null);
        parameters["repoId"] = repoId;
        parameters["q"] = fts;
        parameters["limit"] = limit;

        var sql = store.Kind == StoreKind.Sqlite
            ? $"""
                SELECT c.id, f.path, f.language, c.start_line, c.end_line, c.heading, snippet(chunks_fts, 1, '', '', ' … ', 40) AS snippet
                FROM chunks_fts JOIN chunks c ON c.id = chunks_fts.rowid JOIN files f ON f.content_hash = c.content_hash
                WHERE chunks_fts MATCH @q AND f.repo_id = @repoId {where}
                ORDER BY bm25(chunks_fts, 4.0, 1.0, 3.0) LIMIT @limit
                """
            : $"""
                SELECT c.id, f.path, f.language, c.start_line, c.end_line, c.heading, ts_headline('english', c.text, to_tsquery('english', @q), 'MaxFragments=2, MaxWords=40, MinWords=10, StartSel=, StopSel=, FragmentDelimiter= … ') AS snippet
                FROM chunks c JOIN files f ON f.content_hash = c.content_hash
                WHERE c.tsv @@ to_tsquery('english', @q) AND f.repo_id = @repoId {where}
                ORDER BY ts_rank_cd(c.tsv, to_tsquery('english', @q)) DESC LIMIT @limit
                """;

        return await connection.QueryAsync(
            sql,
            r => new ChunkHit(r.GetInt64("id"), r.GetString("path"), r.GetString("language"), r.GetInt32("start_line"), r.GetInt32("end_line"), r.GetStringOrNull("heading"), r.GetString("snippet").Trim()),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fuses the rankings and remembers the result under a query id for feedback.</summary>
    public async Task<(string QueryId, List<SearchHit> Hits)> FuseAsync(
        DbConnection connection,
        string repoId,
        string query,
        List<SymbolHit> exact,
        List<SymbolHit> symbols,
        List<ChunkHit> chunks,
        IReadOnlyList<(string Key, SearchHit Hit)>? vectorHits,
        int limit,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        List<string> Register<T>(IEnumerable<T> items, Func<T, SearchHit> map)
        {
            var keys = new List<string>();
            foreach (var item in items)
            {
                var hit = map(item);
                candidates.TryAdd(hit.Id, hit);
                keys.Add(hit.Id);
            }

            return keys;
        }

        var rankings = new List<(string, IReadOnlyList<string>, double)>
        {
            ("exact", Register(exact, FromSymbol), 1.2),
            ("symbols", Register(symbols, FromSymbol), 1.0),
            ("text", Register(chunks, FromChunk), 0.9),
        };
        if (vectorHits is { Count: > 0 })
        {
            rankings.Add(("vector", Register(vectorHits, v => v.Hit), 1.0));
        }

        var boosts = await FeedbackBoostsAsync(connection, repoId, query, cancellationToken).ConfigureAwait(false);
        var fused = Rrf.Fuse(rankings);
        var hits = fused
            .Select(f =>
            {
                var hit = candidates[f.Key];
                var boost = boosts.GetValueOrDefault(hit.Path) + boosts.GetValueOrDefault(hit.Id);
                return hit with { Score = Math.Round(f.Score + boost, 6), Ranks = f.Ranks };
            })
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();

        var queryId = Guid.NewGuid().ToString("N")[..12];
        _queries[queryId] = new RememberedQuery(repoId, Tokens(query), hits, DateTimeOffset.UtcNow);
        if (_queries.Count > MaxRemembered)
        {
            foreach (var old in _queries.OrderBy(q => q.Value.At).Take(_queries.Count - MaxRemembered).Select(q => q.Key).ToList())
            {
                _queries.TryRemove(old, out _);
            }
        }

        return (queryId, hits);
    }

    public RememberedQuery? GetQuery(string queryId) => _queries.GetValueOrDefault(queryId);

    public async Task<int> RecordFeedbackAsync(DbConnection connection, RememberedQuery query, IEnumerable<string> hitIdsOrPaths, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var target in hitIdsOrPaths.Distinct(StringComparer.Ordinal))
        {
            var hit = query.Hits.FirstOrDefault(h => h.Id == target || h.Path == target);
            var targets = hit is null ? [target] : new[] { hit.Id, hit.Path };
            foreach (var t in targets.Distinct(StringComparer.Ordinal))
            {
                await connection.ExecuteAsync(
                    $"INSERT INTO feedback (repo_id, query_tokens, target, created_at) VALUES (@repoId, @tokens, @target, {store.Dialect.NowMs})",
                    new { repoId = query.RepoId, tokens = query.QueryTokens, target = t },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

    /// <summary>Targets (paths or hit ids) marked useful for similar past queries, weighted by overlap and age.</summary>
    private static async Task<Dictionary<string, double>> FeedbackBoostsAsync(DbConnection connection, string repoId, string query, CancellationToken cancellationToken)
    {
        var tokens = Tokens(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var boosts = new Dictionary<string, double>(StringComparer.Ordinal);
        if (tokens.Count == 0)
        {
            return boosts;
        }

        var rows = await connection.QueryAsync(
            "SELECT query_tokens, target, created_at FROM feedback WHERE repo_id = @repoId ORDER BY created_at DESC LIMIT 1000",
            r => (Tokens: r.GetString("query_tokens"), Target: r.GetString("target"), At: r.GetTimestamp("created_at")),
            new { repoId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            var past = row.Tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (past.Length == 0)
            {
                continue;
            }

            var overlap = past.Count(tokens.Contains) / (double)Math.Max(past.Length, tokens.Count);
            if (overlap <= 0)
            {
                continue;
            }

            var age = now - row.At;
            var decay = Math.Pow(0.5, age.TotalDays / FeedbackHalfLife.TotalDays);
            // Comparable to one extra top-3 RRF rank when the query matches well and the feedback is recent.
            boosts[row.Target] = boosts.GetValueOrDefault(row.Target) + 0.012 * overlap * decay;
        }

        return boosts;
    }

    public static string Tokens(string query) =>
        string.Join(' ', SqlDialect.SplitIdentifiers(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal));

    public static SearchHit FromSymbol(SymbolHit s) => new(
        $"sym:{s.Path}:{s.FullName}:{s.StartLine}",
        "symbol",
        s.Path,
        s.Language,
        s.StartLine,
        s.EndLine,
        s.FullName,
        $"{s.Kind}: {s.Signature}",
        s.Doc ?? string.Empty,
        0,
        new Dictionary<string, int>(StringComparer.Ordinal));

    public static SearchHit FromChunk(ChunkHit c) => new(
        $"chunk:{c.Path}:{c.StartLine}",
        "text",
        c.Path,
        c.Language,
        c.StartLine,
        c.EndLine,
        c.Heading ?? $"{c.Path}:{c.StartLine}-{c.EndLine}",
        null,
        c.Snippet,
        0,
        new Dictionary<string, int>(StringComparer.Ordinal));

    private static (string Where, Dictionary<string, object?> Parameters) Filters(SearchFilters filters, string fileAlias, string? symbolAlias)
    {
        var where = new List<string>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(filters.Language))
        {
            where.Add($"{fileAlias}.language = @language");
            parameters["language"] = filters.Language.ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(filters.PathPrefix))
        {
            where.Add($"{fileAlias}.path LIKE @pathPrefix");
            parameters["pathPrefix"] = filters.PathPrefix.Replace('\\', '/').TrimStart('/').TrimEnd('/') + "%";
        }

        if (symbolAlias is not null && !string.IsNullOrWhiteSpace(filters.Kind))
        {
            where.Add($"{symbolAlias}.kind = @kind");
            parameters["kind"] = filters.Kind.ToLowerInvariant();
        }

        return (where.Count == 0 ? string.Empty : "AND " + string.Join(" AND ", where), parameters);
    }

    private static SymbolHit MapSymbol(DbDataReader r) => new(
        r.GetInt64("id"),
        r.GetString("path"),
        r.GetString("language"),
        r.GetString("name"),
        r.GetString("full_name"),
        r.GetString("kind"),
        r.GetStringOrNull("container"),
        r.GetString("signature"),
        r.GetStringOrNull("doc"),
        r.GetInt32("start_line"),
        r.GetInt32("end_line"));

    public static async Task<List<SymbolHit>> OutlineAsync(DbConnection connection, string repoId, string path, CancellationToken cancellationToken) =>
        await connection.QueryAsync(
            "SELECT s.id, f.path, f.language, s.name, s.full_name, s.kind, s.container, s.signature, s.doc, s.start_line, s.end_line FROM symbols s JOIN files f ON f.content_hash = s.content_hash WHERE f.repo_id = @repoId AND f.path = @path ORDER BY s.start_line, s.id",
            MapSymbol,
            new { repoId, path },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public static async Task<List<SymbolHit>> ByNameAsync(DbConnection connection, string repoId, string name, int limit, CancellationToken cancellationToken) =>
        await connection.QueryAsync(
            "SELECT s.id, f.path, f.language, s.name, s.full_name, s.kind, s.container, s.signature, s.doc, s.start_line, s.end_line FROM symbols s JOIN files f ON f.content_hash = s.content_hash WHERE f.repo_id = @repoId AND (s.full_name = @name OR s.name = @name) ORDER BY CASE WHEN s.full_name = @name THEN 0 ELSE 1 END, f.path, s.start_line LIMIT @limit",
            MapSymbol,
            new { repoId, name, limit },
            cancellationToken: cancellationToken).ConfigureAwait(false);
}
