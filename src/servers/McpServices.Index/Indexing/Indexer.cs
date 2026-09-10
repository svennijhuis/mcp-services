using System.Diagnostics;
using System.Text;
using McpServices.Index.Git;
using McpServices.Storage;
using Microsoft.Extensions.Logging;

namespace McpServices.Index.Indexing;

public sealed record IndexRunResult(
    string RepoId,
    string Root,
    bool Completed,
    string? SkippedReason,
    int Added,
    int Updated,
    int Removed,
    int Unchanged,
    int Reused,
    int Skipped,
    int Generation,
    string? HeadSha,
    long DurationMs,
    bool UsedGit,
    IReadOnlyList<string> Errors);

/// <summary>
/// One incremental indexing run. Cheap checks first (size + mtime), then content hash, then
/// extraction only for content never seen before. Files are committed in batches so a crash leaves
/// a consistent, merely incomplete index; the next run picks up where it stopped.
/// </summary>
public sealed class Indexer(IKnowledgeStore store, IndexRepository repository, IndexOptions options, ILogger<Indexer> logger)
{
    private const int BatchSize = 100;

    public async Task<IndexRunResult> RunAsync(RepoIdentity identity, bool force, IReadOnlyCollection<string>? onlyPaths, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var lockName = IndexSchema.WriterLockPrefix + identity.RepoId;
        await using var writerLock = await store.TryAcquireWriterLockAsync(lockName, cancellationToken).ConfigureAwait(false);
        if (writerLock is null)
        {
            var holder = await store.DescribeWriterLockAsync(lockName, cancellationToken).ConfigureAwait(false);
            return new IndexRunResult(identity.RepoId, identity.Root, false, $"indexing already in progress ({holder ?? "another process"})", 0, 0, 0, 0, 0, 0, 0, null, watch.ElapsedMilliseconds, false, []);
        }

        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await repository.EnsureRepositoryAsync(connection, identity, cancellationToken).ConfigureAwait(false);
        var generation = await repository.NextGenerationAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);

        if (force)
        {
            await repository.DeleteAllFilesAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
        }

        var existing = await repository.LoadFilesAsync(connection, identity.RepoId, cancellationToken).ConfigureAwait(false);
        var (walked, usedGit) = await FileWalker.WalkAsync(identity.Root, options.MaxFileKb, cancellationToken).ConfigureAwait(false);
        if (onlyPaths is not null)
        {
            var set = new HashSet<string>(onlyPaths, StringComparer.Ordinal);
            walked = walked.Where(f => set.Contains(f.RelativePath)).ToList();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        int added = 0, updated = 0, unchanged = 0, reused = 0, skipped = 0;

        foreach (var batch in walked.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in batch)
            {
                seen.Add(file.RelativePath);
                existing.TryGetValue(file.RelativePath, out var known);
                if (!force && known is not null && known.Size == file.Size && known.MtimeMs == file.MtimeMs)
                {
                    unchanged++;
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{file.RelativePath}: {ex.Message}");
                    skipped++;
                    continue;
                }

                if (Language.IsBinary(bytes))
                {
                    skipped++;
                    if (known is not null)
                    {
                        await repository.DeleteFileAsync(connection, transaction, identity.RepoId, file.RelativePath, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var hash = RepoIdentity.ContentHash(bytes);
                var language = Language.Detect(file.RelativePath);
                if (known is not null && known.ContentHash == hash)
                {
                    // Touched but identical (git checkout, formatter no-op): remember the new mtime, nothing else.
                    await repository.TouchFileAsync(connection, transaction, identity.RepoId, file, generation, cancellationToken).ConfigureAwait(false);
                    unchanged++;
                    continue;
                }

                if (!await repository.ContentExistsAsync(connection, transaction, hash, cancellationToken).ConfigureAwait(false))
                {
                    Extraction extraction;
                    try
                    {
                        extraction = Extractor.Extract(file.RelativePath, language, Decode(bytes));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Extraction failed for {Path}; indexing as plain text", file.RelativePath);
                        errors.Add($"{file.RelativePath}: {ex.Message}");
                        extraction = Extractor.Extract(file.RelativePath, "text", Decode(bytes));
                    }

                    await repository.InsertContentAsync(connection, transaction, hash, language, extraction, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    reused++;
                }

                await repository.UpsertFileAsync(connection, transaction, identity.RepoId, file, hash, language, generation, cancellationToken).ConfigureAwait(false);
                if (known is null)
                {
                    added++;
                }
                else
                {
                    updated++;
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var removed = 0;
        if (onlyPaths is null)
        {
            var gone = existing.Keys.Where(p => !seen.Contains(p)).ToList();
            foreach (var batch in gone.Chunk(BatchSize))
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                foreach (var path in batch)
                {
                    await repository.DeleteFileAsync(connection, transaction, identity.RepoId, path, cancellationToken).ConfigureAwait(false);
                    removed++;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            foreach (var path in onlyPaths.Where(p => existing.ContainsKey(p) && !File.Exists(Path.Combine(identity.Root, p))))
            {
                await repository.DeleteFileAsync(connection, null, identity.RepoId, path, cancellationToken).ConfigureAwait(false);
                removed++;
            }
        }

        if (removed > 0 || force)
        {
            await repository.CollectOrphansAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        var headSha = usedGit ? await GitCli.HeadShaAsync(identity.Root, cancellationToken).ConfigureAwait(false) : null;
        var errorSummary = errors.Count == 0 ? null : $"{errors.Count} file(s) skipped: {string.Join("; ", errors.Take(5))}";
        await repository.FinishRunAsync(connection, identity.RepoId, headSha, watch.ElapsedMilliseconds, errorSummary, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Indexed {Root}: +{Added} ~{Updated} -{Removed} ={Unchanged} in {Ms} ms", identity.Root, added, updated, removed, unchanged, watch.ElapsedMilliseconds);
        return new IndexRunResult(identity.RepoId, identity.Root, true, null, added, updated, removed, unchanged, reused, skipped, generation, headSha, watch.ElapsedMilliseconds, usedGit, errors);
    }

    private static string Decode(byte[] bytes)
    {
        // Honour a BOM; otherwise assume UTF-8 and replace invalid sequences rather than failing the file.
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, throwOnInvalidBytes: false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
