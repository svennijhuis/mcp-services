using System.Data.Common;
using McpServices.Storage;

namespace McpServices.Index.Commit;

/// <summary>SQL for the commit-keyed tables. Derived rows are replaced per commit. Authored rows are not deleted here.</summary>
internal sealed class CommitRepository(IKnowledgeStore store)
{
    public async Task<int> ReplaceDerivedAsync(DbConnection connection, string repoId, string commitSha, CommitGraph graph, bool merge, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM symbol_edge WHERE repo_id = @repoId AND commit_sha = @commitSha", new { repoId, commitSha }, transaction, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM occurrence WHERE repo_id = @repoId AND commit_sha = @commitSha", new { repoId, commitSha }, transaction, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM symbol WHERE repo_id = @repoId AND commit_sha = @commitSha", new { repoId, commitSha }, transaction, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM file WHERE repo_id = @repoId AND commit_sha = @commitSha", new { repoId, commitSha }, transaction, cancellationToken).ConfigureAwait(false);

        foreach (var file in graph.Files)
        {
            await connection.ExecuteAsync(
                "INSERT INTO file (repo_id, commit_sha, path, content_hash, language) VALUES (@repoId, @commitSha, @path, @contentHash, @language)",
                new { repoId, commitSha, path = file.Path, contentHash = file.ContentHash, language = file.Language },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in graph.Symbols)
        {
            if (!symbols.Add(symbol.SymbolKey))
            {
                continue;
            }

            await connection.ExecuteAsync(
                "INSERT INTO symbol (repo_id, commit_sha, symbol_key, scip_symbol, path, kind, name, start_line, start_col, end_line, end_col, content_hash, doc, signature) VALUES (@repoId, @commitSha, @symbolKey, @scipSymbol, @path, @kind, @name, @startLine, @startCol, @endLine, @endCol, @contentHash, @doc, @signature)",
                new
                {
                    repoId,
                    commitSha,
                    symbolKey = symbol.SymbolKey,
                    scipSymbol = symbol.ScipSymbol,
                    path = symbol.Path,
                    kind = symbol.Kind,
                    name = symbol.Name,
                    startLine = symbol.StartLine,
                    startCol = symbol.StartCol,
                    endLine = symbol.EndLine,
                    endCol = symbol.EndCol,
                    contentHash = symbol.ContentHash,
                    doc = symbol.Doc,
                    signature = symbol.Signature,
                },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var occurrence in graph.Occurrences)
        {
            if (!symbols.Contains(occurrence.SymbolKey))
            {
                continue;
            }

            await connection.ExecuteAsync(
                "INSERT INTO occurrence (repo_id, commit_sha, symbol_key, path, start_line, start_col, end_line, end_col, role) VALUES (@repoId, @commitSha, @symbolKey, @path, @startLine, @startCol, @endLine, @endCol, @role)",
                new
                {
                    repoId,
                    commitSha,
                    symbolKey = occurrence.SymbolKey,
                    path = occurrence.Path,
                    startLine = occurrence.StartLine,
                    startCol = occurrence.StartCol,
                    endLine = occurrence.EndLine,
                    endCol = occurrence.EndCol,
                    role = occurrence.Role,
                },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        var edges = new HashSet<(string From, string To, string Kind)>();
        foreach (var edge in graph.Edges)
        {
            if (edge.FromKey == edge.ToKey || !symbols.Contains(edge.FromKey) || !symbols.Contains(edge.ToKey) || !edges.Add((edge.FromKey, edge.ToKey, edge.Kind)))
            {
                continue;
            }

            await connection.ExecuteAsync(
                "INSERT INTO symbol_edge (repo_id, commit_sha, from_key, to_key, kind) VALUES (@repoId, @commitSha, @fromKey, @toKey, @kind)",
                new { repoId, commitSha, fromKey = edge.FromKey, toKey = edge.ToKey, kind = edge.Kind },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        var stale = await MarkStaleAsync(connection, transaction, repoId, commitSha, merge, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stale;
    }

    public async Task<int> MarkStaleAsync(DbConnection connection, DbTransaction transaction, string repoId, string commitSha, bool merge, CancellationToken cancellationToken)
    {
        const string miss = """
            NOT EXISTS (
                SELECT 1 FROM symbol s
                WHERE s.repo_id = rationale.repo_id
                  AND s.commit_sha = @commitSha
                  AND s.symbol_key = rationale.anchor_key
                  AND rationale.symbol_hash IS NOT NULL
                  AND s.content_hash = rationale.symbol_hash)
            """;
        var authored = await connection.ExecuteAsync(
            $"UPDATE rationale SET status = '{RationaleStatus.Stale}' WHERE repo_id = @repoId AND commit_sha = @commitSha AND status IN ('{RationaleStatus.Active}', '{RationaleStatus.Superseded}') AND {miss}",
            new { repoId, commitSha },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (!merge)
        {
            return authored;
        }

        var others = await connection.ExecuteAsync(
            $"UPDATE rationale SET status = '{RationaleStatus.Stale}' WHERE repo_id = @repoId AND status IN ('{RationaleStatus.Active}', '{RationaleStatus.Superseded}') AND {miss}",
            new { repoId, commitSha },
            transaction,
            cancellationToken).ConfigureAwait(false);
        return authored + others;
    }

    public async Task<List<SymbolRow>> SymbolsByNameAsync(DbConnection connection, string repoId, string commitSha, string name, CancellationToken cancellationToken) =>
        await connection.QueryAsync(
            "SELECT symbol_key, scip_symbol, path, kind, name, start_line, start_col, end_line, end_col, content_hash, doc, signature FROM symbol WHERE repo_id = @repoId AND commit_sha = @commitSha AND (symbol_key = @name OR scip_symbol = @name OR name = @name)",
            MapSymbol,
            new { repoId, commitSha, name },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<SymbolRow?> SymbolAsync(DbConnection connection, string repoId, string commitSha, string symbolKey, CancellationToken cancellationToken) =>
        await connection.SingleOrDefaultAsync(
            "SELECT symbol_key, scip_symbol, path, kind, name, start_line, start_col, end_line, end_col, content_hash, doc, signature FROM symbol WHERE repo_id = @repoId AND commit_sha = @commitSha AND symbol_key = @symbolKey",
            MapSymbol,
            new { repoId, commitSha, symbolKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<List<EdgeFact>> EdgesAsync(DbConnection connection, string repoId, string commitSha, CancellationToken cancellationToken) =>
        await connection.QueryAsync(
            "SELECT from_key, to_key, kind FROM symbol_edge WHERE repo_id = @repoId AND commit_sha = @commitSha AND kind IN ('calls', 'implements', 'references')",
            r => new EdgeFact(r.GetString("from_key"), r.GetString("to_key"), r.GetString("kind")),
            new { repoId, commitSha },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<List<RationaleRow>> RationalesAsync(DbConnection connection, string repoId, string anchorKey, CancellationToken cancellationToken) =>
        await connection.QueryAsync(
            "SELECT id, anchor_key, rule_id, commit_sha, body, confidence, source, symbol_hash, hint_path, hint_line, hint_col, status FROM rationale WHERE repo_id = @repoId AND anchor_key = @anchorKey AND status <> 'rejected' ORDER BY id DESC",
            MapRationale,
            new { repoId, anchorKey },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<Dictionary<string, string>> ActiveRulesAsync(DbConnection connection, string repoId, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync(
            "SELECT rule_id, statement FROM business_rule WHERE repo_id = @repoId AND status = 'active'",
            r => (r.GetString("rule_id"), r.GetString("statement")),
            new { repoId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(r => r.Item1, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal);
    }

    public async Task<int> SupersedeActiveAsync(DbConnection connection, DbTransaction transaction, string repoId, string anchorKey, string ruleId, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
            "UPDATE rationale SET status = 'superseded' WHERE repo_id = @repoId AND anchor_key = @anchorKey AND rule_id = @ruleId AND status = 'active'",
            new { repoId, anchorKey, ruleId },
            transaction,
            cancellationToken).ConfigureAwait(false);

    public async Task<long> InsertRationaleAsync(
        DbConnection connection,
        DbTransaction transaction,
        string repoId,
        string anchorKey,
        string ruleId,
        string commitSha,
        string body,
        string confidence,
        string source,
        string? symbolHash,
        string? hintPath,
        int? hintLine,
        int? hintCol,
        string status,
        CancellationToken cancellationToken)
    {
        var values = new
        {
            repoId,
            anchorKey,
            ruleId,
            commitSha,
            body,
            confidence,
            source,
            symbolHash,
            hintPath,
            hintLine,
            hintCol,
            status,
        };
        var columns = "repo_id, anchor_key, rule_id, commit_sha, body, confidence, source, symbol_hash, hint_path, hint_line, hint_col, status, created_at";
        var parameters = $"@repoId, @anchorKey, @ruleId, @commitSha, @body, @confidence, @source, @symbolHash, @hintPath, @hintLine, @hintCol, @status, {store.Dialect.NowMs}";
        if (store.Kind == StoreKind.Postgres)
        {
            return await connection.ScalarAsync<long>(
                $"INSERT INTO rationale ({columns}) VALUES ({parameters}) RETURNING id",
                values,
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(
            $"INSERT INTO rationale ({columns}) VALUES ({parameters})",
            values,
            transaction,
            cancellationToken).ConfigureAwait(false);
        return await connection.ScalarAsync<long>("SELECT last_insert_rowid()", transaction: transaction, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static SymbolRow MapSymbol(DbDataReader reader) => new(
        reader.GetString("symbol_key"),
        reader.GetStringOrNull("scip_symbol"),
        reader.GetString("path"),
        reader.GetString("kind"),
        reader.GetString("name"),
        reader.GetInt32("start_line"),
        reader.GetInt32("start_col"),
        reader.GetInt32("end_line"),
        reader.GetInt32("end_col"),
        reader.GetString("content_hash"),
        reader.GetStringOrNull("doc"),
        reader.GetString("signature"));

    private static RationaleRow MapRationale(DbDataReader reader) => new(
        reader.GetInt64("id"),
        reader.GetString("anchor_key"),
        reader.GetString("rule_id"),
        reader.GetString("commit_sha"),
        reader.GetString("body"),
        reader.GetString("confidence"),
        reader.GetString("source"),
        reader.GetStringOrNull("symbol_hash"),
        reader.GetStringOrNull("hint_path"),
        reader[reader.GetOrdinal("hint_line")] is DBNull or null ? null : reader.GetInt32("hint_line"),
        reader[reader.GetOrdinal("hint_col")] is DBNull or null ? null : reader.GetInt32("hint_col"),
        reader.GetString("status"));
}

internal sealed record SymbolRow(
    string SymbolKey,
    string? ScipSymbol,
    string Path,
    string Kind,
    string Name,
    int StartLine,
    int StartCol,
    int EndLine,
    int EndCol,
    string ContentHash,
    string? Doc,
    string Signature);

internal sealed record RationaleRow(
    long Id,
    string AnchorKey,
    string RuleId,
    string CommitSha,
    string Text,
    string Confidence,
    string Source,
    string? SymbolHash,
    string? HintPath,
    int? HintLine,
    int? HintCol,
    string Status);
