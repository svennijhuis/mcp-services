using McpServices.Index.Git;
using McpServices.Storage;

namespace McpServices.Index.Indexing;

/// <summary>
/// Attached to every answer so agents know whether the index reflects the working tree.
/// <see cref="Stale"/> is <c>null</c> when the check could not decide (huge repo without git).
/// </summary>
public sealed record Freshness(
    bool? Stale,
    string Reason,
    string CheckedWith,
    int ChangedCount,
    IReadOnlyList<string> ChangedFiles,
    DateTimeOffset? LastIndexedAt,
    string? IndexedHeadSha,
    string? CurrentHeadSha,
    string? RefreshAction);

public sealed class FreshnessChecker(IKnowledgeStore store, IndexRepository repository, IndexOptions options)
{
    private const int MaxReportedFiles = 50;

    public async Task<Freshness> CheckAsync(RepoIdentity identity, CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var repo = await repository.GetRepositoryAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
        if (repo?.LastIndexedAt is null)
        {
            return new Freshness(true, "never indexed", "none", 0, [], null, null, null, null);
        }

        var files = await repository.LoadFilesAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
        var topLevel = await GitCli.TopLevelAsync(identity.Root, cancellationToken).ConfigureAwait(false);
        return topLevel is not null
            ? await CheckWithGitAsync(identity, repo, files, cancellationToken).ConfigureAwait(false)
            : await CheckByScanAsync(identity, repo, files, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Freshness> CheckWithGitAsync(RepoIdentity identity, RepositoryRow repo, Dictionary<string, FileRow> files, CancellationToken cancellationToken)
    {
        var head = await GitCli.HeadShaAsync(identity.Root, cancellationToken).ConfigureAwait(false);
        var rules = IgnoreRules.Load(identity.Root, includeGitignore: false);
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        var reasons = new List<string>();

        if (head is not null && repo.HeadSha is not null && head != repo.HeadSha)
        {
            reasons.Add("HEAD moved");
            var diff = await GitCli.RunAsync(identity.Root, ["diff", "--name-only", repo.HeadSha, head], cancellationToken).ConfigureAwait(false);
            if (diff.Success)
            {
                foreach (var path in diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (DiffersFromIndex(identity.Root, path, files, rules))
                    {
                        changed.Add(path);
                    }
                }
            }
        }

        var porcelain = await GitCli.ChangedPathsAsync(identity.Root, cancellationToken).ConfigureAwait(false) ?? [];
        var dirty = 0;
        foreach (var path in porcelain.Distinct(StringComparer.Ordinal))
        {
            if (DiffersFromIndex(identity.Root, path, files, rules))
            {
                changed.Add(path);
                dirty++;
            }
        }

        if (dirty > 0)
        {
            reasons.Add($"{dirty} working-tree change(s) not indexed");
        }

        var stale = changed.Count > 0;
        return new Freshness(
            stale,
            stale ? string.Join("; ", reasons) : "index matches HEAD and working tree",
            "git",
            changed.Count,
            changed.Take(MaxReportedFiles).ToList(),
            repo.LastIndexedAt,
            repo.HeadSha,
            head,
            stale ? "index_repository" : null);
    }

    private async Task<Freshness> CheckByScanAsync(RepoIdentity identity, RepositoryRow repo, Dictionary<string, FileRow> files, CancellationToken cancellationToken)
    {
        var (walked, _) = await FileWalker.WalkAsync(identity.Root, options.MaxFileKb, cancellationToken).ConfigureAwait(false);
        if (walked.Count > options.FreshnessMaxFiles)
        {
            return new Freshness(null, $"repository has {walked.Count} files and no git; freshness not checked (limit {options.FreshnessMaxFiles})", "scan-skipped", 0, [], repo.LastIndexedAt, repo.HeadSha, null, "index_repository");
        }

        var changed = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in walked)
        {
            seen.Add(file.RelativePath);
            if (!files.TryGetValue(file.RelativePath, out var known) || known.Size != file.Size || known.MtimeMs != file.MtimeMs)
            {
                changed.Add(file.RelativePath);
            }
        }

        foreach (var path in files.Keys.Where(p => !seen.Contains(p)))
        {
            changed.Add(path);
        }

        var stale = changed.Count > 0;
        return new Freshness(stale, stale ? $"{changed.Count} file(s) differ from the index" : "index matches the file system", "scan", changed.Count, changed.Take(MaxReportedFiles).ToList(), repo.LastIndexedAt, repo.HeadSha, null, stale ? "index_repository" : null);
    }

    /// <summary>A path counts as changed when it is new, deleted, or its size/mtime differ from the indexed row.</summary>
    private static bool DiffersFromIndex(string root, string relativePath, Dictionary<string, FileRow> files, IgnoreRules rules)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        files.TryGetValue(relativePath, out var known);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return known is not null;
        }

        if (known is null)
        {
            return !rules.IsIgnored(relativePath) && !Directory.Exists(full);
        }

        return known.Size != info.Length || known.MtimeMs != FileWalker.ToMs(info.LastWriteTimeUtc);
    }
}
