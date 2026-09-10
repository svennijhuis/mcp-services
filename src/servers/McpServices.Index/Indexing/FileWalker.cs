using McpServices.Index.Git;

namespace McpServices.Index.Indexing;

public sealed record WalkedFile(string RelativePath, string FullPath, long Size, long MtimeMs);

/// <summary>Enumerates indexable files of a repository, via git when possible, otherwise by walking.</summary>
public static class FileWalker
{
    public static async Task<(IReadOnlyList<WalkedFile> Files, bool UsedGit)> WalkAsync(string root, int maxFileKb, CancellationToken cancellationToken)
    {
        var maxBytes = (long)maxFileKb * 1024;
        var gitFiles = await GitCli.ListFilesAsync(root, cancellationToken).ConfigureAwait(false);
        var usedGit = gitFiles is not null;
        var rules = IgnoreRules.Load(root, includeGitignore: !usedGit);
        var results = new List<WalkedFile>();

        if (gitFiles is not null)
        {
            foreach (var relative in gitFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rules.IsIgnored(relative))
                {
                    continue;
                }

                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(full);
                if (!info.Exists || info.LinkTarget is not null || info.Length > maxBytes)
                {
                    continue;
                }

                results.Add(new WalkedFile(relative, full, info.Length, ToMs(info.LastWriteTimeUtc)));
            }

            return (results, true);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(entry))
                {
                    if (!IgnoreRules.IsExcludedDirectory(Path.GetFileName(entry)) && !rules.IsIgnored(relative + "/"))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                if (rules.IsIgnored(relative))
                {
                    continue;
                }

                var info = new FileInfo(entry);
                if (info.Length > maxBytes)
                {
                    continue;
                }

                results.Add(new WalkedFile(relative, entry, info.Length, ToMs(info.LastWriteTimeUtc)));
            }
        }

        results.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return (results, false);
    }

    public static long ToMs(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
