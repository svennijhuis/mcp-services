using System.IO.Hashing;
using System.Text;

namespace McpServices.Storage;

/// <summary>
/// Stable identity of a checkout: the canonical root path and a short hash of it. Two worktrees of
/// the same repository get different ids (their files differ); the same path always maps to the same id.
/// </summary>
public sealed record RepoIdentity(string RepoId, string Root, string Name)
{
    public static RepoIdentity FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Canonicalize(path);
        var name = Path.GetFileName(root);
        if (string.IsNullOrEmpty(name))
        {
            name = root;
        }

        return new RepoIdentity(Hash(root), root, name);
    }

    public static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var info = new DirectoryInfo(full);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                full = target.FullName;
            }
        }
        catch (IOException)
        {
            // Path may not exist yet; keep the lexical form.
        }
        catch (UnauthorizedAccessException)
        {
        }

        full = Path.TrimEndingDirectorySeparator(full);
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToLowerInvariant() : full;
    }

    public static string Hash(string canonicalRoot)
    {
        var hash = XxHash128.Hash(Encoding.UTF8.GetBytes(canonicalRoot));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>Content hash used for change detection and content-addressed storage.</summary>
    public static string ContentHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(XxHash128.Hash(content));
}
