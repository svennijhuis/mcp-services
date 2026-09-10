using McpServices.FileSystem;
using McpServices.Hosting;

namespace McpServices.FileSystem.Tests;

public sealed class PathGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pathguard-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("pathguard-outside-").FullName;

    [Fact]
    public void Allows_paths_inside_root()
    {
        var guard = new PathGuard([_root]);
        var nested = Path.Combine(_root, "a", "b.txt");

        Assert.Equal(PathGuard.RealPath(nested), guard.Resolve(nested));
        Assert.Equal(PathGuard.RealPath(_root), guard.Resolve(_root));
    }

    [Fact]
    public void Resolves_relative_paths_against_first_root()
    {
        var guard = new PathGuard([_root]);
        Assert.Equal(Path.Combine(PathGuard.RealPath(_root), "sub", "file.txt"), guard.Resolve("sub/file.txt"));
    }

    [Fact]
    public void Denies_paths_outside_root()
    {
        var guard = new PathGuard([_root]);
        var ex = Assert.Throws<ToolException>(() => guard.Resolve(Path.Combine(_outside, "x.txt")));
        Assert.Contains("outside the allowed directories", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Denies_dot_dot_traversal()
    {
        var guard = new PathGuard([_root]);
        Assert.Throws<ToolException>(() => guard.Resolve(Path.Combine(_root, "..", Path.GetFileName(_outside), "x.txt")));
    }

    [Fact]
    public void Denies_sibling_directory_with_same_prefix()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var guard = new PathGuard([_root]);
            Assert.Throws<ToolException>(() => guard.Resolve(Path.Combine(sibling, "x.txt")));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void Denies_symlink_escaping_root()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(_root, "escape");
        File.CreateSymbolicLink(link, _outside);
        var guard = new PathGuard([_root]);

        Assert.Throws<ToolException>(() => guard.Resolve(Path.Combine(link, "secret.txt")));
    }

    [Fact]
    public void Denies_symlinked_parent_for_new_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(_root, "linkdir");
        File.CreateSymbolicLink(link, _outside);
        var guard = new PathGuard([_root]);

        Assert.Throws<ToolException>(() => guard.Resolve(Path.Combine(link, "new", "deeper", "file.txt")));
    }

    [Fact]
    public void Requires_at_least_one_directory()
    {
        Assert.Throws<ServerStartupException>(() => new PathGuard([]));
        Assert.Throws<ServerStartupException>(() => new PathGuard([Path.Combine(_root, "missing")]));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }
}
