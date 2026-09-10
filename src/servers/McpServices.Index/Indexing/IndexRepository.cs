using System.Data.Common;
using McpServices.Storage;

namespace McpServices.Index.Indexing;

public sealed record RepositoryRow(
    string RepoId,
    string Root,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastIndexedAt,
    string? HeadSha,
    int Generation,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    long? LastDurationMs,
    string? LastError);

public sealed record FileRow(string Path, string ContentHash, long Size, long MtimeMs, string Language);

/// <summary>All SQL for repositories, files and content-addressed symbols/chunks.</summary>
public sealed class IndexRepository(IKnowledgeStore store)
{
    public SqlDialect Dialect => store.Dialect;

    public async Task<RepositoryRow?> GetRepositoryAsync(DbConnection connection, string repoId, CancellationToken cancellationToken) =>
        await connection.SingleOrDefaultAsync("SELECT * FROM repositories WHERE repo_id = @repoId", MapRepository, new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<List<RepositoryRow>> ListRepositoriesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.QueryAsync("SELECT * FROM repositories ORDER BY name", MapRepository, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task EnsureRepositoryAsync(DbConnection connection, RepoIdentity identity, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
            $"INSERT INTO repositories (repo_id, root, name, created_at) VALUES (@repoId, @root, @name, {Dialect.NowMs}) {Dialect.Upsert("repo_id", "root", "name")}",
            new { repoId = identity.RepoId, root = identity.Root, name = identity.Name },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<int> NextGenerationAsync(DbConnection connection, string repoId, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync("UPDATE repositories SET generation = generation + 1 WHERE repo_id = @repoId", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await connection.ScalarAsync<int>("SELECT generation FROM repositories WHERE repo_id = @repoId", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task FinishRunAsync(DbConnection connection, string repoId, string? headSha, long durationMs, string? error, CancellationToken cancellationToken)
    {
        var counts = await connection.SingleOrDefaultAsync(
            """
            SELECT
                (SELECT COUNT(*) FROM files WHERE repo_id = @repoId) AS files,
                (SELECT COUNT(*) FROM symbols s WHERE s.content_hash IN (SELECT content_hash FROM files WHERE repo_id = @repoId)) AS symbols,
                (SELECT COUNT(*) FROM chunks c WHERE c.content_hash IN (SELECT content_hash FROM files WHERE repo_id = @repoId)) AS chunks
            """,
            r => new { Files = r.GetInt32("files"), Symbols = r.GetInt32("symbols"), Chunks = r.GetInt32("chunks") },
            new { repoId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            $"UPDATE repositories SET last_indexed_at = {Dialect.NowMs}, head_sha = @headSha, file_count = @files, symbol_count = @symbols, chunk_count = @chunks, last_duration_ms = @durationMs, last_error = @error WHERE repo_id = @repoId",
            new { repoId, headSha, files = counts?.Files ?? 0, symbols = counts?.Symbols ?? 0, chunks = counts?.Chunks ?? 0, durationMs, error },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordErrorAsync(DbConnection connection, string repoId, string error, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync("UPDATE repositories SET last_error = @error WHERE repo_id = @repoId", new { repoId, error }, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<Dictionary<string, FileRow>> LoadFilesAsync(DbConnection connection, string repoId, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync(
            "SELECT path, content_hash, size, mtime_ms, language FROM files WHERE repo_id = @repoId",
            r => new FileRow(r.GetString("path"), r.GetString("content_hash"), r.GetInt64("size"), r.GetInt64("mtime_ms"), r.GetString("language")),
            new { repoId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Path, StringComparer.Ordinal);
    }

    public async Task<FileRow?> GetFileAsync(DbConnection connection, string repoId, string path, CancellationToken cancellationToken) =>
        await connection.SingleOrDefaultAsync(
            "SELECT path, content_hash, size, mtime_ms, language FROM files WHERE repo_id = @repoId AND path = @path",
            r => new FileRow(r.GetString("path"), r.GetString("content_hash"), r.GetInt64("size"), r.GetInt64("mtime_ms"), r.GetString("language")),
            new { repoId, path },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<bool> ContentExistsAsync(DbConnection connection, DbTransaction? transaction, string contentHash, CancellationToken cancellationToken) =>
        await connection.ScalarAsync<long?>("SELECT 1 FROM contents WHERE content_hash = @contentHash", new { contentHash }, transaction, cancellationToken).ConfigureAwait(false) is not null;

    public async Task InsertContentAsync(DbConnection connection, DbTransaction transaction, string contentHash, string language, Extraction extraction, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            $"INSERT INTO contents (content_hash, language, line_count, symbol_count, chunk_count, first_seen_at) VALUES (@contentHash, @language, @lineCount, @symbolCount, @chunkCount, {Dialect.NowMs}) {Dialect.Upsert("content_hash")}",
            new { contentHash, language, lineCount = extraction.LineCount, symbolCount = extraction.Symbols.Count, chunkCount = extraction.Chunks.Count },
            transaction,
            cancellationToken).ConfigureAwait(false);

        foreach (var symbol in extraction.Symbols)
        {
            await connection.ExecuteAsync(
                "INSERT INTO symbols (content_hash, name, full_name, kind, container, signature, doc, start_line, end_line, tokens) VALUES (@contentHash, @name, @fullName, @kind, @container, @signature, @doc, @startLine, @endLine, @tokens)",
                new
                {
                    contentHash,
                    name = symbol.Name,
                    fullName = symbol.FullName,
                    kind = symbol.Kind,
                    container = symbol.Container,
                    signature = symbol.Signature,
                    doc = symbol.Doc,
                    startLine = symbol.StartLine,
                    endLine = symbol.EndLine,
                    tokens = SqlDialect.SplitIdentifiers(symbol.Name + " " + symbol.FullName.Replace('.', ' ')),
                },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in extraction.Chunks)
        {
            await connection.ExecuteAsync(
                "INSERT INTO chunks (content_hash, ordinal, start_line, end_line, heading, text, tokens) VALUES (@contentHash, @ordinal, @startLine, @endLine, @heading, @text, @tokens)",
                new
                {
                    contentHash,
                    ordinal = chunk.Ordinal,
                    startLine = chunk.StartLine,
                    endLine = chunk.EndLine,
                    heading = chunk.Heading,
                    text = chunk.Text,
                    tokens = SqlDialect.SplitIdentifiers(IdentifierWords(chunk.Text)),
                },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpsertFileAsync(DbConnection connection, DbTransaction transaction, string repoId, WalkedFile file, string contentHash, string language, int generation, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
            $"INSERT INTO files (repo_id, path, content_hash, size, mtime_ms, language, generation, indexed_at) VALUES (@repoId, @path, @contentHash, @size, @mtime, @language, @generation, {Dialect.NowMs}) {Dialect.Upsert("repo_id, path", "content_hash", "size", "mtime_ms", "language", "generation", "indexed_at")}",
            new { repoId, path = file.RelativePath, contentHash, size = file.Size, mtime = file.MtimeMs, language, generation },
            transaction,
            cancellationToken).ConfigureAwait(false);

    public async Task TouchFileAsync(DbConnection connection, DbTransaction transaction, string repoId, WalkedFile file, int generation, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
            "UPDATE files SET size = @size, mtime_ms = @mtime, generation = @generation WHERE repo_id = @repoId AND path = @path",
            new { repoId, path = file.RelativePath, size = file.Size, mtime = file.MtimeMs, generation },
            transaction,
            cancellationToken).ConfigureAwait(false);

    public async Task DeleteFileAsync(DbConnection connection, DbTransaction? transaction, string repoId, string path, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync("DELETE FROM files WHERE repo_id = @repoId AND path = @path", new { repoId, path }, transaction, cancellationToken).ConfigureAwait(false);

    public async Task DeleteAllFilesAsync(DbConnection connection, string repoId, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync("DELETE FROM files WHERE repo_id = @repoId", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task DeleteRepositoryAsync(DbConnection connection, string repoId, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync("DELETE FROM files WHERE repo_id = @repoId", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM notes WHERE repo_id = @repoId", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM feedback WHERE repo_id = @repoId", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM related_cache WHERE repo_id = @repoId", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM repositories WHERE repo_id = @repoId", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes content no file references anymore (after deletes, rebuilds or branch switches).</summary>
    public async Task<int> CollectOrphansAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        const string orphan = "content_hash NOT IN (SELECT DISTINCT content_hash FROM files)";
        await connection.ExecuteAsync($"DELETE FROM symbols WHERE {orphan}", cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync($"DELETE FROM chunks WHERE {orphan}", cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync($"DELETE FROM embeddings WHERE {orphan}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync($"DELETE FROM contents WHERE {orphan}", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountOrphansAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.ScalarAsync<int>("SELECT COUNT(*) FROM contents WHERE content_hash NOT IN (SELECT DISTINCT content_hash FROM files)", cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<int> CountDanglingFilesAsync(DbConnection connection, string repoId, CancellationToken cancellationToken) =>
        await connection.ScalarAsync<int>("SELECT COUNT(*) FROM files f WHERE f.repo_id = @repoId AND NOT EXISTS (SELECT 1 FROM contents c WHERE c.content_hash = f.content_hash)", new { repoId }, cancellationToken: cancellationToken).ConfigureAwait(false);

    private static RepositoryRow MapRepository(DbDataReader r) => new(
        r.GetString("repo_id"),
        r.GetString("root"),
        r.GetString("name"),
        r.GetTimestamp("created_at"),
        r.GetTimestampOrNull("last_indexed_at"),
        r.GetStringOrNull("head_sha"),
        r.GetInt32("generation"),
        r.GetInt32("file_count"),
        r.GetInt32("symbol_count"),
        r.GetInt32("chunk_count"),
        r.GetInt64OrNull("last_duration_ms"),
        r.GetStringOrNull("last_error"));

    /// <summary>Identifier-like words of a chunk (deduplicated) so split tokens stay small.</summary>
    private static string IdentifierWords(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (isWord)
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                var word = text[start..i];
                start = -1;
                if (word.Length >= 3 && (word.Any(char.IsUpper) || word.Contains('_')) && seen.Add(word))
                {
                    words.Add(word);
                    if (words.Count >= 400)
                    {
                        break;
                    }
                }
            }
        }

        return string.Join(' ', words);
    }
}
