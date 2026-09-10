using System.Data.Common;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using McpServices.Hosting;
using McpServices.Storage;

namespace McpServices.Learnings;

/// <summary>
/// All SQL for the learnings store. Learnings are deduplicated by fingerprint (outcome + normalized
/// title + repo + files + tool): a repeat bumps <c>occurrences</c> and <c>last_seen_at</c> instead of
/// creating a new row, so corroboration is a count rather than a pile of near-duplicates.
/// </summary>
public sealed class LearningsRepository(IKnowledgeStore store)
{
    public SqlDialect Dialect => store.Dialect;

    public static string Fingerprint(Outcome outcome, string title, string? repo, IReadOnlyList<string> files, string? toolOrSkill)
    {
        var material = string.Join('\n',
            outcome.ToString().ToLowerInvariant(),
            Text.NormalizeTitle(title),
            RepoRef.Normalize(repo) ?? string.Empty,
            string.Join(',', files.OrderBy(f => f, StringComparer.Ordinal)),
            toolOrSkill?.Trim().ToLowerInvariant() ?? string.Empty);
        return Convert.ToHexStringLower(XxHash128.Hash(Encoding.UTF8.GetBytes(material)));
    }

    public async Task<(Learning Learning, bool Created)> RecordAsync(DbConnection connection, NewLearning input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var title = SecretRedactor.Redact(ToolGuard.NotEmpty(input.Title, "title").Trim())!;
        var detail = SecretRedactor.Redact(input.Detail?.Trim()) ?? string.Empty;
        var evidence = SecretRedactor.Redact(input.Evidence?.Trim());
        var files = Text.NormalizeFiles(input.Files);
        var tags = Text.NormalizeTags(input.Tags);
        var repo = RepoRef.Normalize(input.Repo);
        var tool = string.IsNullOrWhiteSpace(input.ToolOrSkill) ? null : input.ToolOrSkill.Trim();
        var category = string.IsNullOrWhiteSpace(input.Category) ? "general" : input.Category.Trim().ToLowerInvariant();
        var fingerprint = Fingerprint(input.Outcome, title, repo, files, tool);
        var tokens = SqlDialect.SplitIdentifiers(string.Join(' ', [title, detail, tool ?? string.Empty, category, .. tags, .. files.Select(Path.GetFileNameWithoutExtension)!]));

        var existing = await connection.SingleOrDefaultAsync("SELECT * FROM learnings WHERE fingerprint = @fingerprint", Map, new { fingerprint }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Keep the richer detail/evidence; a repeat with less text should not erase what we know.
            var mergedDetail = detail.Length > existing.Detail.Length ? detail : existing.Detail;
            var mergedEvidence = evidence is { Length: > 0 } && (existing.Evidence is null || evidence.Length > existing.Evidence.Length) ? evidence : existing.Evidence;
            var mergedTags = existing.Tags.Union(tags, StringComparer.Ordinal).ToList();
            await connection.ExecuteAsync(
                $"""
                UPDATE learnings SET occurrences = occurrences + 1, detail = @detail, evidence = @evidence, tags = @tags, tokens = @tokens,
                    provider = COALESCE(@provider, provider), model = COALESCE(@model, model), last_seen_at = {Dialect.NowMs}, updated_at = {Dialect.NowMs}
                WHERE id = @id
                """,
                new { id = existing.Id, detail = mergedDetail, evidence = mergedEvidence, tags = mergedTags, tokens, provider = input.Provider, model = input.Model },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var updated = await GetAsync(connection, existing.Id, cancellationToken).ConfigureAwait(false);
            return (updated!, false);
        }

        await connection.ExecuteAsync(
            $"""
            INSERT INTO learnings (fingerprint, outcome, category, title, detail, tags, repo, files, tool_or_skill, provider, model, evidence, source, meta, tokens, created_at, updated_at, last_seen_at)
            VALUES (@fingerprint, @outcome, @category, @title, @detail, @tags, @repo, @files, @tool, @provider, @model, @evidence, @source, @meta, @tokens, {Dialect.NowMs}, {Dialect.NowMs}, {Dialect.NowMs})
            """,
            new
            {
                fingerprint,
                outcome = input.Outcome,
                category,
                title,
                detail,
                tags,
                repo,
                files,
                tool,
                provider = input.Provider?.Trim().ToLowerInvariant(),
                model = input.Model?.Trim(),
                evidence,
                source = input.Source,
                meta = string.IsNullOrWhiteSpace(input.Meta) ? "{}" : input.Meta,
                tokens,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var id = await LastIdAsync(connection, "learnings", cancellationToken).ConfigureAwait(false);
        return ((await GetAsync(connection, id, cancellationToken).ConfigureAwait(false))!, true);
    }

    public Task<Learning?> GetAsync(DbConnection connection, long id, CancellationToken cancellationToken) =>
        connection.SingleOrDefaultAsync("SELECT * FROM learnings WHERE id = @id", Map, new { id }, cancellationToken: cancellationToken);

    public async Task<List<Learning>> GetManyAsync(DbConnection connection, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var results = new List<Learning>();
        foreach (var id in ids.Distinct())
        {
            var learning = await GetAsync(connection, id, cancellationToken).ConfigureAwait(false);
            if (learning is not null)
            {
                results.Add(learning);
            }
        }

        return results;
    }

    /// <summary>Full-text query (optional) plus filters. Repo-less learnings are global and always match a repo filter.</summary>
    public Task<List<Learning>> QueryAsync(DbConnection connection, string? query, LearningFilter filter, int limit, CancellationToken cancellationToken)
    {
        var (where, parameters) = Where(filter, "l");
        parameters["limit"] = limit;
        string sql;
        if (string.IsNullOrWhiteSpace(query))
        {
            sql = $"SELECT l.* FROM learnings l WHERE 1 = 1 {where} ORDER BY l.last_seen_at DESC LIMIT @limit";
        }
        else
        {
            var fts = Dialect.FullTextQuery(query);
            if (string.IsNullOrEmpty(fts) || fts == "\"\"")
            {
                return Task.FromResult(new List<Learning>());
            }

            parameters["q"] = fts;
            sql = store.Kind == StoreKind.Sqlite
                ? $"SELECT l.* FROM learnings_fts JOIN learnings l ON l.id = learnings_fts.rowid WHERE learnings_fts MATCH @q {where} ORDER BY bm25(learnings_fts) LIMIT @limit"
                : $"SELECT l.* FROM learnings l WHERE l.tsv @@ to_tsquery('english', @q) {where} ORDER BY ts_rank_cd(l.tsv, to_tsquery('english', @q)) DESC LIMIT @limit";
        }

        return connection.QueryAsync(sql, Map, parameters, cancellationToken: cancellationToken);
    }

    public async Task<int> CountAsync(DbConnection connection, LearningFilter filter, CancellationToken cancellationToken)
    {
        var (where, parameters) = Where(filter, "l");
        return await connection.ScalarAsync<int>($"SELECT COUNT(*) FROM learnings l WHERE 1 = 1 {where}", parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MarkUsefulAsync(DbConnection connection, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var id in ids.Distinct())
        {
            count += await connection.ExecuteAsync($"UPDATE learnings SET useful_count = useful_count + 1, updated_at = {Dialect.NowMs} WHERE id = @id", new { id }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    public async Task<bool> DeleteAsync(DbConnection connection, long id, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync("DELETE FROM learnings WHERE id = @id", new { id }, cancellationToken: cancellationToken).ConfigureAwait(false) > 0;

    public async Task<long> RecordFeedbackAsync(DbConnection connection, string tool, bool worked, string? note, string? repo, string? provider, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            $"INSERT INTO feedback (tool, worked, note, repo, provider, created_at) VALUES (@tool, @worked, @note, @repo, @provider, {Dialect.NowMs})",
            new { tool = tool.Trim(), worked, note = SecretRedactor.Redact(note), repo = RepoRef.Normalize(repo), provider = provider?.Trim().ToLowerInvariant() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await LastIdAsync(connection, "feedback", cancellationToken).ConfigureAwait(false);
    }

    public Task<List<(string Tool, int Worked, int Failed, DateTimeOffset LastAt)>> FeedbackSummaryAsync(DbConnection connection, DateTimeOffset? since, string? repo, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["since"] = since ?? DateTimeOffset.UnixEpoch };
        var repoFilter = string.Empty;
        if (RepoRef.Normalize(repo) is { } normalized)
        {
            repoFilter = "AND (repo = @repo OR repo IS NULL)";
            parameters["repo"] = normalized;
        }

        return connection.QueryAsync(
            $"SELECT tool, SUM(CASE WHEN worked <> 0 THEN 1 ELSE 0 END) AS worked, SUM(CASE WHEN worked = 0 THEN 1 ELSE 0 END) AS failed, MAX(created_at) AS last_at FROM feedback WHERE created_at >= @since {repoFilter} GROUP BY tool ORDER BY failed DESC, worked DESC",
            r => (r.GetString("tool"), r.GetInt32("worked"), r.GetInt32("failed"), r.GetTimestamp("last_at")),
            parameters,
            cancellationToken: cancellationToken);
    }

    public Task<List<FeedbackRow>> RecentFeedbackAsync(DbConnection connection, DateTimeOffset since, int limit, CancellationToken cancellationToken) =>
        connection.QueryAsync(
            "SELECT * FROM feedback WHERE created_at >= @since ORDER BY created_at DESC LIMIT @limit",
            r => new FeedbackRow(r.GetInt64("id"), r.GetString("tool"), r.GetBoolean("worked"), r.GetStringOrNull("note"), r.GetStringOrNull("repo"), r.GetStringOrNull("provider"), r.GetTimestamp("created_at")),
            new { since, limit },
            cancellationToken: cancellationToken);

    // Proposals

    public async Task<long> InsertProposalAsync(DbConnection connection, string targetRepo, string title, string rationale, IReadOnlyList<long> learningIds, IReadOnlyList<string> likelyFiles, IReadOnlyList<string> acceptanceCriteria, string prompt, int corroborations, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            $"""
            INSERT INTO proposals (status, target_repo, title, rationale, learning_ids, likely_files, acceptance_criteria, prompt, corroborations, created_at, updated_at)
            VALUES (@status, @targetRepo, @title, @rationale, @learningIds, @likelyFiles, @criteria, @prompt, @corroborations, {Dialect.NowMs}, {Dialect.NowMs})
            """,
            new { status = StatusName(ProposalStatus.Draft), targetRepo, title, rationale, learningIds = JsonSerializer.Serialize(learningIds), likelyFiles, criteria = acceptanceCriteria, prompt, corroborations },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await LastIdAsync(connection, "proposals", cancellationToken).ConfigureAwait(false);
    }

    public Task<Proposal?> GetProposalAsync(DbConnection connection, long id, CancellationToken cancellationToken) =>
        connection.SingleOrDefaultAsync("SELECT * FROM proposals WHERE id = @id", MapProposal, new { id }, cancellationToken: cancellationToken);

    public Task<List<Proposal>> ListProposalsAsync(DbConnection connection, ProposalStatus? status, int limit, CancellationToken cancellationToken) =>
        status is null
            ? connection.QueryAsync("SELECT * FROM proposals ORDER BY created_at DESC LIMIT @limit", MapProposal, new { limit }, cancellationToken: cancellationToken)
            : connection.QueryAsync("SELECT * FROM proposals WHERE status = @status ORDER BY created_at DESC LIMIT @limit", MapProposal, new { status = StatusName(status.Value), limit }, cancellationToken: cancellationToken);

    public Task<int> UpdateProposalAsync(DbConnection connection, long id, ProposalStatus status, string? dispatchMode, string? externalId, string? prUrl, string? lastError, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            $"""
            UPDATE proposals SET status = @status, dispatch_mode = COALESCE(@dispatchMode, dispatch_mode), external_id = COALESCE(@externalId, external_id),
                pr_url = COALESCE(@prUrl, pr_url), last_error = @lastError, updated_at = {Dialect.NowMs}
            WHERE id = @id
            """,
            new { id, status = StatusName(status), dispatchMode, externalId, prUrl, lastError = SecretRedactor.Redact(lastError) },
            cancellationToken: cancellationToken);

    public async Task<long> InsertDispatchAsync(DbConnection connection, long proposalId, string mode, string requestId, string? externalId, string status, string? detail, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            $"INSERT INTO dispatches (proposal_id, mode, request_id, external_id, status, detail, created_at) VALUES (@proposalId, @mode, @requestId, @externalId, @status, @detail, {Dialect.NowMs})",
            new { proposalId, mode, requestId, externalId, status, detail = SecretRedactor.Redact(Text.Truncate(detail, 4000)) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await LastIdAsync(connection, "dispatches", cancellationToken).ConfigureAwait(false);
    }

    public Task<List<DispatchRow>> ListDispatchesAsync(DbConnection connection, long proposalId, CancellationToken cancellationToken) =>
        connection.QueryAsync(
            "SELECT * FROM dispatches WHERE proposal_id = @proposalId ORDER BY created_at DESC",
            r => new DispatchRow(r.GetInt64("id"), r.GetInt64("proposal_id"), r.GetString("mode"), r.GetString("request_id"), r.GetStringOrNull("external_id"), r.GetString("status"), r.GetStringOrNull("detail"), r.GetTimestamp("created_at")),
            new { proposalId },
            cancellationToken: cancellationToken);

    /// <summary>Real (non dry-run) dispatch attempts since <paramref name="since"/>, for the daily cap.</summary>
    public async Task<int> CountDispatchesSinceAsync(DbConnection connection, DateTimeOffset since, CancellationToken cancellationToken) =>
        await connection.ScalarAsync<int>("SELECT COUNT(*) FROM dispatches WHERE created_at >= @since AND mode <> 'dry-run' AND status <> 'failed'", new { since }, cancellationToken: cancellationToken).ConfigureAwait(false);

    private async Task<long> LastIdAsync(DbConnection connection, string table, CancellationToken cancellationToken) =>
        store.Kind == StoreKind.Sqlite
            ? await connection.ScalarAsync<long>("SELECT last_insert_rowid()", cancellationToken: cancellationToken).ConfigureAwait(false)
            : await connection.ScalarAsync<long>($"SELECT currval(pg_get_serial_sequence('{table}', 'id'))", cancellationToken: cancellationToken).ConfigureAwait(false);

    private static (string Where, Dictionary<string, object?> Parameters) Where(LearningFilter filter, string alias)
    {
        var sb = new StringBuilder();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (RepoRef.Normalize(filter.Repo) is { } repo)
        {
            sb.Append($" AND ({alias}.repo = @repo OR {alias}.repo IS NULL)");
            parameters["repo"] = repo;
        }

        if (filter.Outcome is { } outcome)
        {
            sb.Append($" AND {alias}.outcome = @outcome");
            parameters["outcome"] = outcome;
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            sb.Append($" AND {alias}.category = @category");
            parameters["category"] = filter.Category.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            sb.Append($" AND {alias}.tags LIKE @tag");
            parameters["tag"] = "%\"" + filter.Tag.Trim().ToLowerInvariant() + "\"%";
        }

        if (!string.IsNullOrWhiteSpace(filter.ToolOrSkill))
        {
            sb.Append($" AND LOWER({alias}.tool_or_skill) = @tool");
            parameters["tool"] = filter.ToolOrSkill.Trim().ToLowerInvariant();
        }

        if (filter.Since is { } since)
        {
            sb.Append($" AND {alias}.last_seen_at >= @since");
            parameters["since"] = since;
        }

        return (sb.ToString(), parameters);
    }

    private static Learning Map(DbDataReader r) => new(
        r.GetInt64("id"),
        r.GetString("fingerprint"),
        Enum.Parse<Outcome>(r.GetString("outcome"), ignoreCase: true),
        r.GetString("category"),
        r.GetString("title"),
        r.GetString("detail"),
        r.GetStringList("tags"),
        r.GetStringOrNull("repo"),
        r.GetStringList("files"),
        r.GetStringOrNull("tool_or_skill"),
        r.GetStringOrNull("provider"),
        r.GetStringOrNull("model"),
        r.GetStringOrNull("evidence"),
        r.GetString("source"),
        r.GetString("meta"),
        r.GetInt32("occurrences"),
        r.GetInt32("useful_count"),
        r.GetTimestamp("created_at"),
        r.GetTimestamp("updated_at"),
        r.GetTimestamp("last_seen_at"));

    private static Proposal MapProposal(DbDataReader r) => new(
        r.GetInt64("id"),
        ParseStatus(r.GetString("status")),
        r.GetString("target_repo"),
        r.GetString("title"),
        r.GetString("rationale"),
        JsonSerializer.Deserialize<List<long>>(r.GetString("learning_ids")) ?? [],
        r.GetStringList("likely_files"),
        r.GetStringList("acceptance_criteria"),
        r.GetString("prompt"),
        r.GetInt32("corroborations"),
        r.GetStringOrNull("dispatch_mode"),
        r.GetStringOrNull("external_id"),
        r.GetStringOrNull("pr_url"),
        r.GetStringOrNull("last_error"),
        r.GetTimestamp("created_at"),
        r.GetTimestamp("updated_at"));

    public static string StatusName(ProposalStatus status) => status switch
    {
        ProposalStatus.PrOpened => "pr_opened",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static ProposalStatus ParseStatus(string value) =>
        value.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "draft" => ProposalStatus.Draft,
            "dispatched" => ProposalStatus.Dispatched,
            "propened" => ProposalStatus.PrOpened,
            "merged" => ProposalStatus.Merged,
            "rejected" => ProposalStatus.Rejected,
            "failed" => ProposalStatus.Failed,
            _ => throw new ToolException($"Unknown proposal status '{value}'."),
        };
}
