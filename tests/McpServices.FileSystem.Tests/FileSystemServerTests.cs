using System.Text.Json;
using McpServices.TestSupport;

namespace McpServices.FileSystem.Tests;

public sealed class FileSystemServerTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("fs-server-").FullName;
    private ServerFixture _server = null!;

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "hello.txt"), "line1\nline2\nline3\n");
        Directory.CreateDirectory(Path.Combine(_root, "src", "nested"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "a.cs"), "class A {}\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "nested", "b.cs"), "class B {}\n");
        _server = await ServerFixture.StartAsync("McpServices.FileSystem", [_root]);
    }

    [Fact]
    public async Task Exposes_expected_tools()
    {
        var names = await _server.ToolNamesAsync();
        string[] expected =
        [
            "create_directory", "directory_tree", "edit_file", "get_file_info", "list_allowed_directories", "list_directory",
            "list_directory_with_sizes", "move_file", "read_media_file", "read_multiple_files", "read_text_file", "search_files",
            "server_info", "write_file",
        ];
        Assert.Equal(expected, names);
    }

    [Fact]
    public async Task Reads_head_and_tail()
    {
        Assert.Equal("line1", await _server.CallTextAsync("read_text_file", new { path = Path.Combine(_root, "hello.txt"), head = 1 }));
        Assert.Equal("line3", await _server.CallTextAsync("read_text_file", new { path = Path.Combine(_root, "hello.txt"), tail = 1 }));
    }

    [Fact]
    public async Task Denies_outside_paths()
    {
        var error = await _server.CallExpectingErrorAsync("read_text_file", new { path = "/etc/hostname" });
        Assert.Contains("outside the allowed directories", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_edits_and_reads_back()
    {
        var file = Path.Combine(_root, "new", "file.txt");
        var written = await _server.CallJsonAsync("write_file", new { path = file, content = "alpha\nbeta\n" });
        Assert.True(written.GetProperty("created").GetBoolean());

        var preview = await _server.CallJsonAsync("edit_file", new { path = file, edits = new[] { new { oldText = "beta", newText = "gamma" } }, dryRun = true });
        Assert.False(preview.GetProperty("applied").GetBoolean());
        Assert.Contains("+gamma", preview.GetProperty("diff").GetString(), StringComparison.Ordinal);
        Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(file));

        var applied = await _server.CallJsonAsync("edit_file", new { path = file, edits = new[] { new { oldText = "beta", newText = "gamma" } } });
        Assert.True(applied.GetProperty("applied").GetBoolean());
        Assert.Equal("alpha\ngamma\n", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Searches_with_globs_and_excludes()
    {
        var all = await _server.CallJsonAsync("search_files", new { path = _root, pattern = "**/*.cs" });
        Assert.Equal(2, all.GetProperty("totalMatches").GetInt32());

        var filtered = await _server.CallJsonAsync("search_files", new { path = _root, pattern = "**/*.cs", excludePatterns = new[] { "nested" } });
        Assert.Equal(1, filtered.GetProperty("totalMatches").GetInt32());
        Assert.EndsWith("a.cs", filtered.GetProperty("matches")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builds_directory_tree()
    {
        var tree = await _server.CallJsonAsync("directory_tree", new { path = _root, excludePatterns = new[] { "nested" } });
        var src = tree.GetProperty("tree").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "src");
        var names = src.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["a.cs"], names);
    }

    [Fact]
    public async Task Moves_and_reports_info()
    {
        var from = Path.Combine(_root, "hello.txt");
        var to = Path.Combine(_root, "moved", "hello2.txt");
        await _server.CallJsonAsync("move_file", new { source = from, destination = to });
        Assert.False(File.Exists(from));

        var info = await _server.CallJsonAsync("get_file_info", new { path = to });
        Assert.Equal("file", info.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Number, info.GetProperty("sizeBytes").ValueKind);
    }

    [Fact]
    public async Task Reads_multiple_files_with_partial_failures()
    {
        var result = await _server.CallJsonAsync("read_multiple_files", new { paths = new[] { Path.Combine(_root, "src", "a.cs"), Path.Combine(_root, "missing.txt") } });
        var files = result.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);
        Assert.True(files[0].TryGetProperty("content", out _));
        Assert.True(files[1].TryGetProperty("error", out _));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
