using System.Diagnostics;
using System.Text.Json;
using McpServices.TestSupport;

namespace McpServices.Learnings.Tests;

/// <summary>
/// The cursor-cli mode with stub <c>agent</c> and <c>gh</c> executables on PATH and a local bare
/// repository as origin, plus the non-MCP CLI entry points. Unix only (shell stubs).
/// </summary>
public sealed class CliDispatchFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcp-learnings-cli-" + Guid.NewGuid().ToString("N"));

    public string BareRepo => Path.Combine(Root, "origin.git");

    public string Bin => Path.Combine(Root, "bin");

    public string StorePath => Path.Combine(Root, "learnings.db");

    public ServerFixture? Server { get; private set; }

    public bool Supported => !OperatingSystem.IsWindows() && Server is not null;

    public async Task InitializeAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Bin);
        var seed = Path.Combine(Root, "seed");
        Directory.CreateDirectory(seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# Target repo\n");
        if (!await GitAsync(seed, "init", "-q", "-b", "main") ||
            !await GitAsync(seed, "add", "-A") ||
            !await GitAsync(seed, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init") ||
            !await GitAsync(Root, "clone", "-q", "--bare", seed, BareRepo))
        {
            return;
        }

        // Stub Cursor CLI: "implements" the proposal by writing the prompt into a file.
        await WriteScriptAsync(Path.Combine(Bin, "agent"), "#!/bin/sh\nprintf '%s\\n' \"$@\" > AGENT_ARGS.txt\nprintf 'Applied learnings\\n' > LEARNINGS_APPLIED.md\necho '{\"result\":\"ok\"}'\n");
        // Stub GitHub CLI: records the arguments and prints a PR URL.
        await WriteScriptAsync(Path.Combine(Bin, "gh"), $"#!/bin/sh\nprintf '%s\\n' \"$@\" > \"{Path.Combine(Root, "gh-args.txt")}\"\necho https://github.com/test/target/pull/42\n");

        Server = await ServerFixture.StartAsync(
            "McpServices.Learnings",
            [
                "--repo", "test/target",
                "--store", $"sqlite:{StorePath}",
                "--data-dir", Root,
                "--dispatch", "auto",
                "--dispatch-mode", "cursor-cli",
                "--min-corroborations", "2",
                "--max-dispatches-per-day", "1",
                "--log-level", "Warning",
            ],
            new Dictionary<string, string?>
            {
                ["PATH"] = Bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["MCP_LEARNINGS_CLONE_URL"] = BareRepo,
            });
    }

    private static async Task WriteScriptAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public static async Task<bool> GitAsync(string cwd, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class CliDispatchTests(CliDispatchFixture fixture) : IClassFixture<CliDispatchFixture>
{
    [Fact]
    public async Task Auto_policy_dispatches_via_cursor_cli_and_opens_a_draft_pr()
    {
        if (!fixture.Supported)
        {
            return;
        }

        var server = fixture.Server!;
        await server.CallJsonAsync("record_learning", new { outcome = "failed", title = "README lacks the local setup steps", category = "docs" });
        var first = await server.CallJsonAsync("create_proposal", new { query = "README setup" });
        Assert.False(first.TryGetProperty("dispatched", out _));
        Assert.Contains("2 are required", first.GetProperty("note").GetString(), StringComparison.Ordinal);

        await server.CallJsonAsync("record_learning", new { outcome = "failed", title = "README lacks the local setup steps", category = "docs" });
        var second = await server.CallJsonAsync("create_proposal", new { query = "README setup" });
        var dispatched = second.GetProperty("dispatched");
        Assert.Equal("cursor-cli", dispatched.GetProperty("mode").GetString());
        Assert.Equal("https://github.com/test/target/pull/42", dispatched.GetProperty("result").GetProperty("prUrl").GetString());
        Assert.Equal("prOpened", second.GetProperty("proposal").GetProperty("status").GetString());

        var ghArgs = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "gh-args.txt"));
        Assert.Contains("--draft", ghArgs, StringComparison.Ordinal);
        Assert.Contains("--base\nmain", ghArgs, StringComparison.Ordinal);

        // The stub agent's change was committed and pushed to origin on a cursor/learnings-* branch.
        var branches = await RunGitAsync(fixture.BareRepo, "branch", "--list", "cursor/learnings-*");
        Assert.Contains("cursor/learnings-", branches, StringComparison.Ordinal);
        var files = await RunGitAsync(fixture.BareRepo, "ls-tree", "--name-only", branches.Trim().TrimStart('*').Trim());
        Assert.Contains("LEARNINGS_APPLIED.md", files, StringComparison.Ordinal);
        Assert.DoesNotContain(".mcp-learnings-prompt.md", files, StringComparison.Ordinal);

        // Daily cap of 1 reached: the next proposal is created but not dispatched.
        await server.CallJsonAsync("record_learning", new { outcome = "worked", title = "Pin the SDK version in global.json", category = "tooling", evidence = "a" });
        await server.CallJsonAsync("record_learning", new { outcome = "worked", title = "Pin the SDK version in global.json", category = "tooling", evidence = "b" });
        var capped = await server.CallJsonAsync("create_proposal", new { query = "global.json SDK" });
        Assert.False(capped.TryGetProperty("dispatched", out _));
        Assert.Contains("Daily dispatch cap", capped.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_mode_records_and_reports_without_mcp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = Path.Combine(Path.GetTempPath(), "mcp-learnings-clistore-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var recorded = await RunCliAsync("record", "--failed", "Forgot to export PATH for dotnet", "--tag", "shell", "--tool", "dotnet", "--store", $"sqlite:{store}", "--quiet");
            Assert.True(recorded.GetProperty("created").GetBoolean());
            Assert.Equal("failed", recorded.GetProperty("learning").GetProperty("outcome").GetString());
            Assert.Equal("cli", recorded.GetProperty("learning").GetProperty("source").GetString());

            var stats = await RunCliAsync("stats", "--store", $"sqlite:{store}", "--quiet");
            Assert.Equal(1, stats.GetProperty("total").GetInt32());
            Assert.Equal(1, stats.GetProperty("failed").GetInt32());

            var recommend = await RunCliAsync("recommend", "set up dotnet in the shell", "--store", $"sqlite:{store}", "--quiet");
            Assert.Single(recommend.GetProperty("avoid").EnumerateArray());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(store)!, Path.GetFileName(store) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    private static async Task<JsonElement> RunCliAsync(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "McpServices.Learnings.dll");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add(dll);
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {stderr}");
        return JsonDocument.Parse(stdout).RootElement.Clone();
    }

    private static async Task<string> RunGitAsync(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
