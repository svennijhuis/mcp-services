using System.Data.Common;
using McpServices.Index.Indexing;
using McpServices.Storage;

namespace McpServices.Index.Search;

public sealed record NoteRow(long Id, string Text, IReadOnlyList<string> Files, IReadOnlyList<string> FileHashes, IReadOnlyList<string> Tags, int UsefulCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record RecalledNote(long Id, string Note, IReadOnlyList<string> Files, IReadOnlyList<string> Tags, int UsefulCount, DateTimeOffset UpdatedAt, bool PossiblyStale, IReadOnlyList<string> ChangedFiles);

/// <summary>
/// Free-form knowledge about a codebase ("ordering goes through OrderService.Submit"), pinned to
/// the content hashes of the files it mentions so <c>recall</c> can flag it as possibly stale.
/// </summary>
public sealed class NotesService(IKnowledgeStore store, IndexRepository repository)
{
    public async Task<long> RememberAsync(DbConnection connection, string repoId, string note, IReadOnlyList<string> files, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        var normalizedFiles = files.Select(f => f.Replace('\\', '/').TrimStart('/')).Distinct(StringComparer.Ordinal).ToList();
        var hashes = new List<string>();
        foreach (var file in normalizedFiles)
        {
            var row = await repository.GetFileAsync(connection, repoId, file, cancellationToken).ConfigureAwait(false);
            hashes.Add(row?.ContentHash ?? string.Empty);
        }

        var redacted = SecretRedactor.Redact(note)!;
        var normalizedTags = tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var tokens = SqlDialect.SplitIdentifiers(redacted + " " + string.Join(' ', normalizedFiles.Select(Path.GetFileNameWithoutExtension)));

        await connection.ExecuteAsync(
            $"INSERT INTO notes (repo_id, note, files, file_hashes, tags, tokens, created_at, updated_at) VALUES (@repoId, @note, @files, @hashes, @tags, @tokens, {store.Dialect.NowMs}, {store.Dialect.NowMs})",
            new { repoId, note = redacted, files = normalizedFiles, hashes, tags = normalizedTags, tokens },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return store.Kind == StoreKind.Sqlite
            ? await connection.ScalarAsync<long>("SELECT last_insert_rowid()", cancellationToken: cancellationToken).ConfigureAwait(false)
            : await connection.ScalarAsync<long>("SELECT currval(pg_get_serial_sequence('notes', 'id'))", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<RecalledNote>> RecallAsync(DbConnection connection, string repoId, string? query, string? tag, int limit, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["repoId"] = repoId, ["limit"] = limit };
        var tagFilter = string.Empty;
        if (!string.IsNullOrWhiteSpace(tag))
        {
            tagFilter = "AND n.tags LIKE @tag";
            parameters["tag"] = "%\"" + tag.Trim().ToLowerInvariant() + "\"%";
        }

        string sql;
        if (string.IsNullOrWhiteSpace(query))
        {
            sql = $"SELECT n.* FROM notes n WHERE n.repo_id = @repoId {tagFilter} ORDER BY n.useful_count DESC, n.updated_at DESC LIMIT @limit";
        }
        else
        {
            var fts = store.Dialect.FullTextQuery(query);
            parameters["q"] = fts;
            sql = store.Kind == StoreKind.Sqlite
                ? $"SELECT n.* FROM notes_fts JOIN notes n ON n.id = notes_fts.rowid WHERE notes_fts MATCH @q AND n.repo_id = @repoId {tagFilter} ORDER BY bm25(notes_fts) - (n.useful_count * 0.1) LIMIT @limit"
                : $"SELECT n.* FROM notes n WHERE n.tsv @@ to_tsquery('english', @q) AND n.repo_id = @repoId {tagFilter} ORDER BY ts_rank_cd(n.tsv, to_tsquery('english', @q)) + (n.useful_count * 0.1) DESC LIMIT @limit";
        }

        var notes = await connection.QueryAsync(sql, Map, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<RecalledNote>();
        foreach (var note in notes)
        {
            var changed = new List<string>();
            for (var i = 0; i < note.Files.Count; i++)
            {
                var current = await repository.GetFileAsync(connection, repoId, note.Files[i], cancellationToken).ConfigureAwait(false);
                var stored = i < note.FileHashes.Count ? note.FileHashes[i] : string.Empty;
                if (current is null || (stored.Length > 0 && current.ContentHash != stored))
                {
                    changed.Add(note.Files[i]);
                }
            }

            results.Add(new RecalledNote(note.Id, note.Text, note.Files, note.Tags, note.UsefulCount, note.UpdatedAt, changed.Count > 0, changed));
        }

        return results;
    }

    public async Task<bool> ForgetAsync(DbConnection connection, string repoId, long id, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync("DELETE FROM notes WHERE repo_id = @repoId AND id = @id", new { repoId, id }, cancellationToken: cancellationToken).ConfigureAwait(false) > 0;

    public async Task<int> MarkUsefulAsync(DbConnection connection, string repoId, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var id in ids)
        {
            count += await connection.ExecuteAsync($"UPDATE notes SET useful_count = useful_count + 1, updated_at = {store.Dialect.NowMs} WHERE repo_id = @repoId AND id = @id", new { repoId, id }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>Re-pins notes to the current hashes of files that still exist; called after indexing when asked.</summary>
    public async Task<int> RepinAsync(DbConnection connection, string repoId, long id, CancellationToken cancellationToken)
    {
        var note = await connection.SingleOrDefaultAsync("SELECT * FROM notes WHERE repo_id = @repoId AND id = @id", Map, new { repoId, id }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (note is null)
        {
            return 0;
        }

        var hashes = new List<string>();
        foreach (var file in note.Files)
        {
            var row = await repository.GetFileAsync(connection, repoId, file, cancellationToken).ConfigureAwait(false);
            hashes.Add(row?.ContentHash ?? string.Empty);
        }

        return await connection.ExecuteAsync($"UPDATE notes SET file_hashes = @hashes, updated_at = {store.Dialect.NowMs} WHERE id = @id", new { hashes, id }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static NoteRow Map(DbDataReader r) => new(
        r.GetInt64("id"),
        r.GetString("note"),
        r.GetStringList("files"),
        r.GetStringList("file_hashes"),
        r.GetStringList("tags"),
        r.GetInt32("useful_count"),
        r.GetTimestamp("created_at"),
        r.GetTimestamp("updated_at"));
}
