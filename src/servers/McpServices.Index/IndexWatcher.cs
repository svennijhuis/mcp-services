using McpServices.Index.Indexing;
using McpServices.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServices.Index;

/// <summary>
/// Optional <c>--watch</c>: file system events on the configured roots schedule a debounced
/// background refresh. Falls back to periodic polling when the watcher overflows (huge repos).
/// Queries are never blocked by it.
/// </summary>
public sealed class IndexWatcher(IndexCoordinator coordinator, IndexOptions options, ILogger<IndexWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Watch || options.Roots.Count == 0)
        {
            return;
        }

        var watchers = new List<FileSystemWatcher>();
        var pending = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var overflow = false;
        var gate = new object();

        foreach (var root in options.Roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };

                void Schedule(object? _, FileSystemEventArgs e)
                {
                    if (IsIgnoredPath(root, e.FullPath))
                    {
                        return;
                    }

                    lock (gate)
                    {
                        pending[root] = DateTimeOffset.UtcNow + Debounce;
                    }
                }

                watcher.Changed += Schedule;
                watcher.Created += Schedule;
                watcher.Deleted += Schedule;
                watcher.Renamed += (s, e) => Schedule(s, e);
                watcher.Error += (_, e) =>
                {
                    overflow = true;
                    logger.LogWarning(e.GetException(), "File watcher for {Root} overflowed; switching to polling", root);
                };
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
                logger.LogInformation("Watching {Root} for changes", root);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Cannot watch {Root}; polling instead", root);
                overflow = true;
            }
        }

        try
        {
            var lastPoll = DateTimeOffset.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                List<string> due;
                lock (gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    due = pending.Where(p => p.Value <= now).Select(p => p.Key).ToList();
                    foreach (var root in due)
                    {
                        pending.Remove(root);
                    }
                }

                if (overflow && DateTimeOffset.UtcNow - lastPoll > PollInterval)
                {
                    lastPoll = DateTimeOffset.UtcNow;
                    due.AddRange(options.Roots.Except(due, StringComparer.Ordinal));
                }

                foreach (var root in due)
                {
                    coordinator.StartBackground(RepoIdentity.FromPath(root));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private static bool IsIgnoredPath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        return relative.Split('/').Any(IgnoreRules.IsExcludedDirectory) || relative.EndsWith(".lock", StringComparison.Ordinal) || relative.EndsWith(".tmp", StringComparison.Ordinal);
    }
}
