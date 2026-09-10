using System.ComponentModel;
using System.Text;
using McpServices.Hosting;
using Microsoft.Extensions.FileSystemGlobbing;
using ModelContextProtocol.Server;

namespace McpServices.FileSystem;

[McpServerToolType]
public sealed class FileSystemTools(PathGuard guard)
{
    private const int MaxTextBytes = 10 * 1024 * 1024;
    private const int MaxMediaBytes = 25 * 1024 * 1024;
    private const int MaxTreeEntries = 5000;

    [McpServerTool(Name = "read_text_file", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Read text file")]
    [Description("Read the complete contents of a text file as UTF-8. Use 'head' to read only the first N lines or 'tail' for the last N lines (not both). Only works within allowed directories.")]
    public async Task<string> ReadTextFile(
        [Description("Absolute path (or path relative to the first allowed directory).")] string path,
        [Description("If set, return only the first N lines.")] int? head = null,
        [Description("If set, return only the last N lines.")] int? tail = null,
        CancellationToken cancellationToken = default)
    {
        if (head is not null && tail is not null)
        {
            throw new ToolException("Specify either 'head' or 'tail', not both.");
        }

        var real = guard.Resolve(path);
        EnsureFile(real, path);
        EnsureSize(real, MaxTextBytes);

        if (head is > 0)
        {
            var lines = new List<string>(head.Value);
            await foreach (var line in File.ReadLinesAsync(real, cancellationToken).ConfigureAwait(false))
            {
                lines.Add(line);
                if (lines.Count >= head.Value)
                {
                    break;
                }
            }

            return string.Join('\n', lines);
        }

        if (tail is > 0)
        {
            var all = await File.ReadAllLinesAsync(real, cancellationToken).ConfigureAwait(false);
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - tail.Value)));
        }

        return await File.ReadAllTextAsync(real, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "read_media_file", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Read media file")]
    [Description("Read an image or audio file and return it base64-encoded together with its MIME type.")]
    public async Task<object> ReadMediaFile([Description("Path to the media file.")] string path, CancellationToken cancellationToken = default)
    {
        var real = guard.Resolve(path);
        EnsureFile(real, path);
        EnsureSize(real, MaxMediaBytes);
        var bytes = await File.ReadAllBytesAsync(real, cancellationToken).ConfigureAwait(false);
        return new { path = real, mimeType = MimeTypes.FromPath(real), sizeBytes = bytes.Length, base64 = Convert.ToBase64String(bytes) };
    }

    [McpServerTool(Name = "read_multiple_files", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Read multiple files")]
    [Description("Read several text files at once. Failures for individual files are reported per file instead of aborting the whole call.")]
    public async Task<object> ReadMultipleFiles([Description("Paths to read.")] string[] paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0 || paths.Length > 100)
        {
            throw new ToolException("Provide between 1 and 100 paths.");
        }

        var results = new List<object>(paths.Length);
        foreach (var p in paths)
        {
            try
            {
                var real = guard.Resolve(p);
                EnsureFile(real, p);
                EnsureSize(real, MaxTextBytes);
                results.Add(new { path = p, content = await File.ReadAllTextAsync(real, cancellationToken).ConfigureAwait(false) });
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
            {
                results.Add(new { path = p, error = ex.Message });
            }
        }

        return new { files = results };
    }

    [McpServerTool(Name = "write_file", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, Title = "Write file")]
    [Description("Create a new file or completely overwrite an existing file with the given content. Parent directories are created. Prefer edit_file for partial changes.")]
    public async Task<object> WriteFile([Description("Destination path.")] string path, [Description("Full file content.")] string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var real = guard.Resolve(path);
        if (Directory.Exists(real))
        {
            throw new ToolException($"'{path}' is a directory.");
        }

        var existed = File.Exists(real);
        await AtomicWriteAsync(real, content, cancellationToken).ConfigureAwait(false);
        return new { path = real, bytesWritten = Encoding.UTF8.GetByteCount(content), created = !existed };
    }

    [McpServerTool(Name = "edit_file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, Title = "Edit file")]
    [Description("Make line-based edits to a text file. Each edit replaces an exact sequence of lines (oldText) with newText; whitespace-insensitive matching is used as a fallback while preserving indentation. Returns a unified diff. Set dryRun=true to preview without writing.")]
    public async Task<object> EditFile(
        [Description("File to edit.")] string path,
        [Description("Ordered list of edits; each has oldText (must match once) and newText.")] TextEdit[] edits,
        [Description("Preview the diff without modifying the file.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var real = guard.Resolve(path);
        EnsureFile(real, path);
        EnsureSize(real, MaxTextBytes);

        var original = await File.ReadAllTextAsync(real, cancellationToken).ConfigureAwait(false);
        var result = FileEditor.Apply(original, edits, path);
        if (!dryRun && result.Diff.Length > 0)
        {
            await AtomicWriteAsync(real, result.NewContent, cancellationToken).ConfigureAwait(false);
        }

        return new { path = real, dryRun, applied = !dryRun && result.Diff.Length > 0, edits = result.AppliedEdits, changed = result.Diff.Length > 0, diff = result.Diff };
    }

    [McpServerTool(Name = "create_directory", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Create directory")]
    [Description("Create a directory, including any missing parents. Succeeds silently if it already exists.")]
    public object CreateDirectory([Description("Directory path.")] string path)
    {
        var real = guard.Resolve(path);
        if (File.Exists(real))
        {
            throw new ToolException($"'{path}' exists and is a file.");
        }

        var existed = Directory.Exists(real);
        Directory.CreateDirectory(real);
        return new { path = real, created = !existed };
    }

    [McpServerTool(Name = "list_directory", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List directory")]
    [Description("List files and directories directly inside a directory. Entries are prefixed with [FILE] or [DIR].")]
    public object ListDirectory([Description("Directory path.")] string path)
    {
        var real = guard.Resolve(path);
        EnsureDirectory(real, path);
        var entries = new DirectoryInfo(real).EnumerateFileSystemInfos()
            .OrderBy(e => e is FileInfo)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => (e is DirectoryInfo ? "[DIR]  " : "[FILE] ") + e.Name)
            .ToList();
        return new { path = real, count = entries.Count, entries };
    }

    [McpServerTool(Name = "list_directory_with_sizes", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List directory with sizes")]
    [Description("List a directory with file sizes and modification times, sorted by name or size, plus totals.")]
    public object ListDirectoryWithSizes(
        [Description("Directory path.")] string path,
        [Description("Sort order: 'name' (default) or 'size'.")] string sortBy = "name")
    {
        var real = guard.Resolve(path);
        EnsureDirectory(real, path);
        var infos = new DirectoryInfo(real).EnumerateFileSystemInfos().Select(e => new
        {
            name = e.Name,
            type = e is DirectoryInfo ? "directory" : "file",
            sizeBytes = e is FileInfo f ? f.Length : 0L,
            modified = e.LastWriteTimeUtc,
        });

        var sorted = sortBy.Equals("size", StringComparison.OrdinalIgnoreCase)
            ? infos.OrderByDescending(e => e.sizeBytes).ThenBy(e => e.name, StringComparer.Ordinal).ToList()
            : infos.OrderBy(e => e.type).ThenBy(e => e.name, StringComparer.Ordinal).ToList();

        return new
        {
            path = real,
            files = sorted.Count(e => e.type == "file"),
            directories = sorted.Count(e => e.type == "directory"),
            totalSizeBytes = sorted.Sum(e => e.sizeBytes),
            entries = sorted,
        };
    }

    [McpServerTool(Name = "directory_tree", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Directory tree")]
    [Description("Recursive JSON tree of a directory. Each entry has name, type ('file'|'directory') and, for directories, children. Use excludePatterns (glob, e.g. 'node_modules', '**/bin') to keep the output small. Capped at 5000 entries.")]
    public object DirectoryTree(
        [Description("Root directory.")] string path,
        [Description("Glob patterns to exclude, matched against paths relative to the root.")] string[]? excludePatterns = null,
        [Description("Maximum depth (default 10).")] int maxDepth = 10)
    {
        var real = guard.Resolve(path);
        EnsureDirectory(real, path);
        var exclude = BuildMatcher(excludePatterns);
        var budget = MaxTreeEntries;
        var truncated = false;

        List<object> Build(string dir, int depth)
        {
            var children = new List<object>();
            if (depth > maxDepth)
            {
                return children;
            }

            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.Ordinal))
            {
                if (budget-- <= 0)
                {
                    truncated = true;
                    return children;
                }

                var relative = Path.GetRelativePath(real, entry.FullName).Replace('\\', '/');
                if (exclude is not null && IsExcluded(exclude, relative, entry is DirectoryInfo))
                {
                    continue;
                }

                if (entry is DirectoryInfo && entry.LinkTarget is null)
                {
                    children.Add(new { name = entry.Name, type = "directory", children = Build(entry.FullName, depth + 1) });
                }
                else
                {
                    children.Add(new { name = entry.Name, type = entry is DirectoryInfo ? "directory" : "file" });
                }
            }

            return children;
        }

        var tree = Build(real, 1);
        return new { path = real, truncated, tree };
    }

    [McpServerTool(Name = "move_file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, Title = "Move or rename")]
    [Description("Move or rename a file or directory. Fails if the destination already exists. Both paths must be inside allowed directories.")]
    public object MoveFile([Description("Existing path.")] string source, [Description("New path.")] string destination)
    {
        var from = guard.Resolve(source);
        var to = guard.Resolve(destination);
        if (File.Exists(to) || Directory.Exists(to))
        {
            throw new ToolException($"Destination already exists: '{destination}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else if (File.Exists(from))
        {
            File.Move(from, to);
        }
        else
        {
            throw new ToolException($"Source not found: '{source}'.");
        }

        return new { source = from, destination = to };
    }

    [McpServerTool(Name = "search_files", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Search files")]
    [Description("Recursively find files and directories whose relative path matches a glob pattern ('*.cs' for the root only, '**/*.cs' for all subdirectories). A plain word is treated as '**/*word*'. Returns full paths, paginated.")]
    public object SearchFiles(
        [Description("Directory to search from.")] string path,
        [Description("Glob pattern relative to 'path'.")] string pattern,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null)
    {
        var real = guard.Resolve(path);
        EnsureDirectory(real, path);
        ToolGuard.NotEmpty(pattern, "pattern");

        var effective = pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('/', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal)
            ? pattern
            : $"**/*{pattern}*";
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase).AddInclude(effective);
        var exclude = BuildMatcher(excludePatterns);

        var matches = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var entry in new DirectoryInfo(real).EnumerateFileSystemInfos("*", options))
        {
            var relative = Path.GetRelativePath(real, entry.FullName).Replace('\\', '/');
            if (exclude is not null && IsExcluded(exclude, relative, entry is DirectoryInfo))
            {
                continue;
            }

            if (matcher.Match(relative).HasMatches)
            {
                matches.Add(entry.FullName);
            }
        }

        matches.Sort(StringComparer.Ordinal);
        var page = Paging.Page(matches, pageToken, pageSize);
        return new { path = real, pattern = effective, totalMatches = page.TotalCount, nextPageToken = page.NextPageToken, matches = page.Items };
    }

    [McpServerTool(Name = "get_file_info", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get file info")]
    [Description("Metadata for a file or directory: size, timestamps, type, permissions and symlink target.")]
    public object GetFileInfo([Description("Path to inspect.")] string path)
    {
        var real = guard.Resolve(path);
        FileSystemInfo info = Directory.Exists(real) ? new DirectoryInfo(real) : new FileInfo(real);
        if (!info.Exists)
        {
            throw new ToolException($"Not found: '{path}'.");
        }

        return new
        {
            path = real,
            type = info is DirectoryInfo ? "directory" : "file",
            sizeBytes = info is FileInfo f ? f.Length : (long?)null,
            created = info.CreationTimeUtc,
            modified = info.LastWriteTimeUtc,
            accessed = info.LastAccessTimeUtc,
            isReadOnly = (info.Attributes & FileAttributes.ReadOnly) != 0,
            isHidden = (info.Attributes & FileAttributes.Hidden) != 0 || info.Name.StartsWith('.'),
            linkTarget = info.LinkTarget,
            unixMode = OperatingSystem.IsWindows() ? null : Convert.ToString((int)File.GetUnixFileMode(real), 8),
            mimeType = info is FileInfo ? MimeTypes.FromPath(real) : null,
        };
    }

    [McpServerTool(Name = "list_allowed_directories", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List allowed directories")]
    [Description("Directories this server may access. Everything below them is reachable; everything else is denied.")]
    public object ListAllowedDirectories() => new { allowedDirectories = guard.AllowedDirectories };

    private static Matcher? BuildMatcher(string[]? patterns)
    {
        if (patterns is null || patterns.Length == 0)
        {
            return null;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var p in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var pattern = p.Trim().TrimEnd('/');
            matcher.AddInclude(pattern.Contains('/', StringComparison.Ordinal) || pattern.Contains('*', StringComparison.Ordinal) ? pattern : $"**/{pattern}");
            matcher.AddInclude(pattern.EndsWith("/**", StringComparison.Ordinal) ? pattern : $"{(pattern.Contains('/', StringComparison.Ordinal) ? pattern : "**/" + pattern)}/**");
        }

        return matcher;
    }

    private static bool IsExcluded(Matcher exclude, string relative, bool isDirectory) =>
        exclude.Match(isDirectory ? relative + "/" : relative).HasMatches || exclude.Match(relative).HasMatches;

    private static void EnsureFile(string real, string display)
    {
        if (Directory.Exists(real))
        {
            throw new ToolException($"'{display}' is a directory, not a file.");
        }

        if (!File.Exists(real))
        {
            throw new ToolException($"File not found: '{display}'.");
        }
    }

    private static void EnsureDirectory(string real, string display)
    {
        if (!Directory.Exists(real))
        {
            throw new ToolException(File.Exists(real) ? $"'{display}' is a file, not a directory." : $"Directory not found: '{display}'.");
        }
    }

    private static void EnsureSize(string real, long max)
    {
        var length = new FileInfo(real).Length;
        if (length > max)
        {
            throw new ToolException($"File is {length:N0} bytes, larger than the {max:N0} byte limit. Use head/tail for a partial read.");
        }
    }

    /// <summary>Writes to a temp file in the same directory and renames it into place so readers never see a partial file.</summary>
    internal static async Task AtomicWriteAsync(string real, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(real)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(real)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, real, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
