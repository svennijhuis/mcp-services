using System.Data.Common;
using System.Text;
using McpServices.Hosting;
using McpServices.Index.Git;
using McpServices.Index.Indexing;
using McpServices.Storage;
using Microsoft.Extensions.Logging;

namespace McpServices.Index.Commit;

/// <summary>
/// Commit-keyed index beside the content-hash index. <c>reindex_commit</c> rebuilds derived rows
/// for one commit and never deletes rationale or business_rule. <c>search_code</c> does not call this.
/// </summary>
public sealed class CommitKnowledge(IKnowledgeStore store, IndexRepository repository, IndexOptions options, ILogger<CommitKnowledge> logger)
{
    public const string Hint = "Hints from calls, implements, and references to depth 2. Not a proof.";

    private const int BlastDepth = 2;
    private const int BlastCap = 200;

    public async Task<ReindexCommitResult> ReindexAsync(RepoIdentity identity, string? commit, CancellationToken cancellationToken)
    {
        var lockName = IndexSchema.WriterLockPrefix + identity.RepoId;
        await using var writerLock = await store.TryAcquireWriterLockAsync(lockName, cancellationToken).ConfigureAwait(false);
        if (writerLock is null)
        {
            var holder = await store.DescribeWriterLockAsync(lockName, cancellationToken).ConfigureAwait(false);
            return new ReindexCommitResult(false, $"indexing already in progress ({holder ?? "another process"})", identity.RepoId, null, null, 0, 0, 0, 0, 0, false);
        }

        var sha = await RequireCommitAsync(identity.Root, commit, cancellationToken).ConfigureAwait(false);
        var merge = await GitCli.ParentCountAsync(identity.Root, sha, cancellationToken).ConfigureAwait(false) >= 2;
        var graph = await BuildAsync(identity.Root, sha, cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await repository.EnsureRepositoryAsync(connection, identity, cancellationToken).ConfigureAwait(false);
        var commits = new CommitRepository(store);
        var stale = await commits.ReplaceDerivedAsync(connection, identity.RepoId, sha, graph.Graph, merge, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Indexed commit {Sha} of {Root} from {Source}: {Symbols} symbols, {Stale} rationale rows marked stale", sha, identity.Root, graph.Source, graph.Graph.Symbols.Count, stale);
        return new ReindexCommitResult(true, null, identity.RepoId, sha, graph.Source, graph.Graph.Files.Count, graph.Graph.Symbols.Count, graph.Graph.Occurrences.Count, graph.Graph.Edges.Count, stale, merge);
    }

    public async Task<WhyResult> WhyAsync(RepoIdentity identity, string anchor, string? commit, bool includeStale, string? ruleId, CancellationToken cancellationToken)
    {
        var sha = await RequireCommitAsync(identity.Root, commit, cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var commits = new CommitRepository(store);
        var (key, symbol) = await ResolveAsync(commits, connection, identity.RepoId, sha, anchor, cancellationToken).ConfigureAwait(false);
        var rows = await commits.RationalesAsync(connection, identity.RepoId, key, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            rows = rows.Where(r => r.RuleId == ruleId.Trim()).ToList();
        }

        var visible = Visible(rows, symbol?.ContentHash, includeStale);
        return new WhyResult(identity.RepoId, sha, key, visible.Count == 0, visible.Select(row => ToWhy(row, symbol)).ToList());
    }

    public async Task<BlastRadiusResult> BlastAsync(RepoIdentity identity, string anchor, string? commit, CancellationToken cancellationToken)
    {
        var sha = await RequireCommitAsync(identity.Root, commit, cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var commits = new CommitRepository(store);
        var (key, symbol) = await ResolveAsync(commits, connection, identity.RepoId, sha, anchor, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return new BlastRadiusResult(identity.RepoId, sha, key, true, false, Hint, []);
        }

        var edges = await commits.EdgesAsync(connection, identity.RepoId, sha, cancellationToken).ConfigureAwait(false);
        var rules = await commits.ActiveRulesAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
        var visited = Walk(key, edges);
        var truncated = visited.Count > BlastCap;
        var rows = new List<BlastSymbol>();
        foreach (var visit in visited.Take(BlastCap))
        {
            var row = visit.Key == key ? symbol : await commits.SymbolAsync(connection, identity.RepoId, sha, visit.Key, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                continue;
            }

            var rationales = Visible(await commits.RationalesAsync(connection, identity.RepoId, row.SymbolKey, cancellationToken).ConfigureAwait(false), row.ContentHash, includeStale: false);
            var attached = new List<BlastRule>();
            foreach (var rationale in rationales)
            {
                if (rationale.RuleId.Length > 0 && rules.TryGetValue(rationale.RuleId, out var statement))
                {
                    attached.Add(new BlastRule(rationale.RuleId, statement));
                }
            }

            rows.Add(new BlastSymbol(row.SymbolKey, row.Name, row.Kind, row.Path, row.StartLine, visit.Depth, visit.Via, attached));
        }

        var hint = truncated ? Hint + " The walk stopped at 200 symbols." : Hint;
        return new BlastRadiusResult(identity.RepoId, sha, key, false, truncated, hint, rows);
    }

    public async Task<UpsertRationaleResult> UpsertAsync(
        RepoIdentity identity,
        string anchor,
        string text,
        string confidence,
        string source,
        string? commit,
        string? ruleId,
        string? symbolHash,
        bool forceActive,
        string? hintPath,
        int? hintLine,
        int? hintCol,
        CancellationToken cancellationToken)
    {
        var body = RationaleText.Normalize(text);
        var confidenceText = RationaleText.Required(confidence, "confidence");
        var sourceText = RationaleText.Required(source, "source");
        var rule = string.IsNullOrWhiteSpace(ruleId) ? string.Empty : ruleId.Trim();
        if (hintLine is < 1)
        {
            throw new ToolException("hintLine must be 1 or greater when it is set.");
        }

        var lockName = IndexSchema.WriterLockPrefix + identity.RepoId;
        await using var writerLock = await store.TryAcquireWriterLockAsync(lockName, cancellationToken).ConfigureAwait(false);
        if (writerLock is null)
        {
            throw new ToolException("indexing already in progress");
        }

        var sha = await RequireCommitAsync(identity.Root, commit, cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await repository.EnsureRepositoryAsync(connection, identity, cancellationToken).ConfigureAwait(false);
        var commits = new CommitRepository(store);
        var (key, symbol) = await ResolveAsync(commits, connection, identity.RepoId, sha, anchor, cancellationToken).ConfigureAwait(false);
        var requested = string.IsNullOrWhiteSpace(symbolHash) ? null : symbolHash.Trim();
        var mismatch = symbol is null || (requested is not null && !string.Equals(requested, symbol.ContentHash, StringComparison.Ordinal));
        // hash mismatch inserts as stale unless force_active=true
        var status = mismatch && !forceActive ? RationaleStatus.Stale : RationaleStatus.Active;
        var storedHash = status == RationaleStatus.Active && symbol is not null
            ? symbol.ContentHash
            : symbol?.ContentHash ?? requested;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var superseded = 0;
        if (status == RationaleStatus.Active)
        {
            superseded = await commits.SupersedeActiveAsync(connection, transaction, identity.RepoId, key, rule, cancellationToken).ConfigureAwait(false);
        }

        long id;
        try
        {
            id = await commits.InsertRationaleAsync(connection, transaction, identity.RepoId, key, rule, sha, body, confidenceText, sourceText, storedHash, string.IsNullOrWhiteSpace(hintPath) ? null : hintPath.Trim(), hintLine, hintCol, status, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new ToolException("The database rejected the rationale row: " + ex.Message, ex);
        }

        return new UpsertRationaleResult(id, identity.RepoId, sha, key, status, superseded > 0);
    }

    internal static List<RationaleRow> Visible(IReadOnlyList<RationaleRow> rows, string? symbolHash, bool includeStale)
    {
        var visible = new List<RationaleRow>();
        foreach (var group in rows.GroupBy(row => row.RuleId, StringComparer.Ordinal))
        {
            var matched = group.FirstOrDefault(row => row.Status == RationaleStatus.Active && HashMatches(row, symbolHash))
                ?? group.FirstOrDefault(row => row.Status == RationaleStatus.Superseded && HashMatches(row, symbolHash));
            if (matched is not null)
            {
                visible.Add(matched);
            }

            if (!includeStale)
            {
                continue;
            }

            foreach (var row in group)
            {
                if (visible.Contains(row))
                {
                    continue;
                }

                if (row.Status == RationaleStatus.Stale || (row.Status == RationaleStatus.Active && !HashMatches(row, symbolHash)))
                {
                    visible.Add(row);
                }
            }
        }

        return visible;
    }

    private static bool HashMatches(RationaleRow row, string? symbolHash) =>
        symbolHash is not null && row.SymbolHash is not null && string.Equals(row.SymbolHash, symbolHash, StringComparison.Ordinal);

    private static WhyItem ToWhy(RationaleRow row, SymbolRow? symbol) => new(
        row.Id,
        row.Text,
        row.Confidence,
        row.Source,
        row.Status,
        row.RuleId,
        row.SymbolHash,
        HashMatches(row, symbol?.ContentHash),
        row.HintPath,
        row.HintLine,
        row.HintCol,
        row.CommitSha);

    private static List<BlastVisit> Walk(string start, IReadOnlyList<EdgeFact> edges)
    {
        var outgoing = edges.ToLookup(edge => edge.FromKey, StringComparer.Ordinal);
        var incoming = edges.ToLookup(edge => edge.ToKey, StringComparer.Ordinal);
        var visited = new List<BlastVisit> { new(start, 0, null) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var index = 0;
        while (index < visited.Count)
        {
            var current = visited[index++];
            if (current.Depth >= BlastDepth)
            {
                continue;
            }

            foreach (var edge in outgoing[current.Key])
            {
                TryAdd(visited, seen, edge.ToKey, current.Depth + 1, edge.Kind + " out");
            }

            foreach (var edge in incoming[current.Key])
            {
                TryAdd(visited, seen, edge.FromKey, current.Depth + 1, edge.Kind + " in");
            }
        }

        return visited;
    }

    private static void TryAdd(List<BlastVisit> visited, HashSet<string> seen, string key, int depth, string via)
    {
        if (seen.Add(key))
        {
            visited.Add(new BlastVisit(key, depth, via));
        }
    }

    private async Task<(string Key, SymbolRow? Symbol)> ResolveAsync(CommitRepository commits, DbConnection connection, string repoId, string sha, string anchor, CancellationToken cancellationToken)
    {
        var name = anchor.Trim();
        var matches = await commits.SymbolsByNameAsync(connection, repoId, sha, name, cancellationToken).ConfigureAwait(false);
        var exact = matches.FirstOrDefault(row => row.SymbolKey == name || row.ScipSymbol == name);
        if (exact is not null)
        {
            return (exact.SymbolKey, exact);
        }

        var byName = matches.Where(row => row.Name == name).DistinctBy(row => row.SymbolKey).ToList();
        if (byName.Count == 1)
        {
            return (byName[0].SymbolKey, byName[0]);
        }

        if (byName.Count > 1)
        {
            var sample = string.Join(", ", byName.Take(5).Select(row => row.Path + ":" + row.StartLine));
            throw new ToolException($"Anchor '{name}' matches {byName.Count} symbols ({sample}). Pass the symbol key.");
        }

        return (name, null);
    }

    private async Task<(CommitGraph Graph, string Source)> BuildAsync(string root, string sha, CancellationToken cancellationToken)
    {
        var tree = await CommitTreeReader.ReadAsync(root, sha, cancellationToken).ConfigureAwait(false);
        if (!tree.Success)
        {
            throw new ToolException(tree.Error ?? "Could not read that commit.");
        }

        var rules = IgnoreRules.Load(root, includeGitignore: false);
        var maxBytes = (long)options.MaxFileKb * 1024;
        var kept = new List<(string Path, string Language, string Text)>();
        var graph = new CommitGraph();
        foreach (var file in tree.Files)
        {
            if (rules.IsIgnored(file.Path) || file.Bytes.LongLength > maxBytes || Language.IsBinary(file.Bytes))
            {
                continue;
            }

            var text = Decode(file.Bytes);
            var language = Language.Detect(file.Path);
            graph.Files.Add(new FileFact(file.Path, RepoIdentity.ContentHash(file.Bytes), language));
            kept.Add((file.Path, language, text));
        }

        if (ScipIndexReader.TryFind(root, out var scipPath))
        {
            ScipIndexReader.Apply(graph, scipPath, kept.ToDictionary(file => file.Path, file => file.Text, StringComparer.Ordinal));
            return (graph, "scip");
        }

        foreach (var file in kept)
        {
            SyntaxGraphParser.AddFile(graph, file.Path, file.Language, file.Text);
        }

        SyntaxGraphParser.Resolve(graph);
        return (graph, "syntax");
    }

    private static async Task<string> RequireCommitAsync(string root, string? commit, CancellationToken cancellationToken)
    {
        if (!GitCli.IsAvailable)
        {
            throw new ToolException("This tool needs git so it can read a commit.");
        }

        var revision = string.IsNullOrWhiteSpace(commit) ? "HEAD" : commit.Trim();
        var sha = await GitCli.ResolveCommitAsync(root, revision, cancellationToken).ConfigureAwait(false);
        return sha ?? throw new ToolException($"Unknown commit '{revision}'.");
    }

    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, throwOnInvalidBytes: false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private sealed record BlastVisit(string Key, int Depth, string? Via);
}

public sealed record ReindexCommitResult(bool Completed, string? SkippedReason, string RepoId, string? CommitSha, string? Source, int Files, int Symbols, int Occurrences, int Edges, int RationaleMarkedStale, bool Merge);

public sealed record WhyItem(long Id, string Text, string Confidence, string Source, string Status, string RuleId, string? SymbolHash, bool HashMatches, string? HintPath, int? HintLine, int? HintCol, string AuthoredCommit);

public sealed record WhyResult(string RepoId, string CommitSha, string Anchor, bool Unknown, IReadOnlyList<WhyItem> Rationale);

public sealed record BlastRule(string RuleId, string Statement);

public sealed record BlastSymbol(string SymbolKey, string Name, string Kind, string Path, int StartLine, int Depth, string? Via, IReadOnlyList<BlastRule> Rules);

public sealed record BlastRadiusResult(string RepoId, string CommitSha, string Anchor, bool Unknown, bool Truncated, string Hint, IReadOnlyList<BlastSymbol> Symbols);

public sealed record UpsertRationaleResult(long Id, string RepoId, string CommitSha, string Anchor, string Status, bool SupersededPrevious);
