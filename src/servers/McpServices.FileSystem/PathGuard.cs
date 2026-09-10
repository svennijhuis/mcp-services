using McpServices.Hosting;

namespace McpServices.FileSystem;

/// <summary>
/// Whitelist-based access control. Every path a tool touches is normalized, has its symlinks
/// resolved (including intermediate directories) and must land inside one of the allowed roots.
/// Not-yet-existing paths are validated through their nearest existing ancestor so a write can
/// never escape via a symlinked parent.
/// </summary>
public sealed class PathGuard
{
    private readonly StringComparison _comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly List<string> _roots;

    public PathGuard(IEnumerable<string> allowedDirectories)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectories);
        _roots = [];
        foreach (var dir in allowedDirectories)
        {
            var full = Path.GetFullPath(ExpandHome(dir));
            if (!Directory.Exists(full))
            {
                throw new ServerStartupException($"Allowed directory does not exist: {dir}");
            }

            _roots.Add(TrimSeparator(RealPath(full)));
        }

        if (_roots.Count == 0)
        {
            throw new ServerStartupException("At least one allowed directory is required. Pass directories as arguments or set MCP_FS_ALLOWED_DIRS.");
        }
    }

    public IReadOnlyList<string> AllowedDirectories => _roots;

    /// <summary>Validates <paramref name="requested"/> and returns its real, absolute path.</summary>
    public string Resolve(string requested)
    {
        ToolGuard.NotEmpty(requested, "path");
        var expanded = ExpandHome(requested);
        var absolute = Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : Path.GetFullPath(Path.Combine(_roots[0], expanded));
        var real = RealPath(absolute);

        if (!IsInsideAllowedRoot(real))
        {
            throw new ToolException($"Access denied: '{requested}' is outside the allowed directories. Use list_allowed_directories to see what is reachable.");
        }

        return real;
    }

    private bool IsInsideAllowedRoot(string realPath)
    {
        var candidate = TrimSeparator(realPath);
        foreach (var root in _roots)
        {
            if (candidate.Equals(root, _comparison))
            {
                return true;
            }

            if (candidate.StartsWith(root + Path.DirectorySeparatorChar, _comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves symlinks segment by segment (like realpath), keeping the trailing segments that do not exist yet.
    /// </summary>
    public static string RealPath(string absolutePath)
    {
        var root = Path.GetPathRoot(absolutePath) ?? string.Empty;
        var segments = absolutePath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var guard = 0;

        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists && !File.Exists(next) && info.LinkTarget is null)
            {
                // Nothing below this point exists; append the remaining segments verbatim.
                current = Path.Combine([current, .. segments[i..]]);
                break;
            }

            if (info.LinkTarget is not null)
            {
                if (++guard > 40)
                {
                    throw new ToolException($"Too many levels of symbolic links while resolving '{absolutePath}'.");
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                             ?? throw new ToolException($"Broken symbolic link: '{next}'.");
                // Restart resolution from the resolved target plus the remaining segments so
                // links inside the target directory are resolved as well.
                var rest = segments[(i + 1)..];
                return rest.Length == 0 ? target : RealPath(Path.Combine([target, .. rest]));
            }

            current = next;
        }

        return Path.GetFullPath(current);
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    private static string TrimSeparator(string path) =>
        path.Length > 1 && (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)) && Path.GetPathRoot(path) != path
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
}
