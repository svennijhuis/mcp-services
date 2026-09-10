using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using McpServices.Hosting;
using McpServices.Index.Git;
using McpServices.Storage;

namespace McpServices.Index.Search;

public sealed record RelatedFile(string Path, int CoChanges, double Confidence);

/// <summary>
/// Co-change analysis from git history: files that were committed together with the requested
/// file. Commit lists are cached per HEAD in the store (and in memory), so repeated calls are cheap.
/// </summary>
public sealed class RelatedFilesService(IKnowledgeStore store)
{
    private const int MaxCommits = 800;
    private const int MaxFilesPerCommit = 40;

    private readonly ConcurrentDictionary<string, (string HeadSha, IReadOnlyList<IReadOnlyList<string>> Commits)> _memory = new(StringComparer.Ordinal);

    public async Task<(IReadOnlyList<RelatedFile> Related, int CommitsAnalyzed, string? HeadSha)> FindAsync(DbConnection connection, RepoIdentity identity, string path, int limit, CancellationToken cancellationToken)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var head = await GitCli.HeadShaAsync(identity.Root, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return ([], 0, null);
        }

        var commits = await LoadCommitsAsync(connection, identity, head, cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var appearances = 0;
        foreach (var files in commits)
        {
            if (!files.Contains(normalized, StringComparer.Ordinal))
            {
                continue;
            }

            appearances++;
            foreach (var other in files)
            {
                if (other != normalized)
                {
                    counts[other] = counts.GetValueOrDefault(other) + 1;
                }
            }
        }

        var related = counts
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(c => new RelatedFile(c.Key, c.Value, appearances == 0 ? 0 : Math.Round(c.Value / (double)appearances, 3)))
            .ToList();
        return (related, commits.Count, head);
    }

    private async Task<IReadOnlyList<IReadOnlyList<string>>> LoadCommitsAsync(DbConnection connection, RepoIdentity identity, string head, CancellationToken cancellationToken)
    {
        if (_memory.TryGetValue(identity.RepoId, out var cached) && cached.HeadSha == head)
        {
            return cached.Commits;
        }

        var stored = await connection.SingleOrDefaultAsync(
            "SELECT head_sha, payload FROM related_cache WHERE repo_id = @repoId",
            r => new { Head = r.GetString("head_sha"), Payload = r.GetString("payload") },
            new { repoId = identity.RepoId },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<IReadOnlyList<string>>? commits = null;
        if (stored is not null && stored.Head == head)
        {
            try
            {
                commits = JsonSerializer.Deserialize<List<List<string>>>(stored.Payload, ToolJson.Options)?.Cast<IReadOnlyList<string>>().ToList();
            }
            catch (JsonException)
            {
                commits = null;
            }
        }

        if (commits is null)
        {
            var raw = await GitCli.RecentCommitFilesAsync(identity.Root, MaxCommits, cancellationToken).ConfigureAwait(false) ?? [];
            commits = raw.Where(files => files.Count is > 1 and <= MaxFilesPerCommit).ToList();
            await connection.ExecuteAsync(
                $"INSERT INTO related_cache (repo_id, head_sha, payload, created_at) VALUES (@repoId, @head, @payload, {store.Dialect.NowMs}) {store.Dialect.Upsert("repo_id", "head_sha", "payload", "created_at")}",
                new { repoId = identity.RepoId, head, payload = JsonSerializer.Serialize(commits, ToolJson.Options) },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        _memory[identity.RepoId] = (head, commits);
        return commits;
    }
}
