using System.Diagnostics;
using System.Text.Json;
using McpServices.TestSupport;
using ModelContextProtocol.Protocol;

namespace McpServices.Index.Tests;

/// <summary>A small git repository plus one server process shared by the tests in this class.</summary>
public sealed class IndexServerFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcp-index-repo-" + Guid.NewGuid().ToString("N"));

    public string StorePath { get; } = Path.Combine(Path.GetTempPath(), "mcp-index-store-" + Guid.NewGuid().ToString("N") + ".db");

    public ServerFixture Server { get; private set; } = null!;

    public bool HasGit { get; private set; }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Root, "src", "Orders"));
        Directory.CreateDirectory(Path.Combine(Root, "docs"));
        Directory.CreateDirectory(Path.Combine(Root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(Root, "src", "Orders", "OrderService.cs"), """
            namespace Shop.Orders;

            /// <summary>Submits orders through the repository.</summary>
            public sealed class OrderService(IOrderRepository repository)
            {
                /// <summary>Submit an order; this is the single entry point for ordering.</summary>
                public async Task<Guid> SubmitAsync(Order order, CancellationToken cancellationToken)
                {
                    await repository.SaveAsync(order, cancellationToken);
                    return order.Id;
                }

                public int PendingCount { get; private set; }
            }

            public interface IOrderRepository
            {
                Task SaveAsync(Order order, CancellationToken cancellationToken);
            }

            public sealed record Order(Guid Id, decimal Total);
            """);
        await File.WriteAllTextAsync(Path.Combine(Root, "src", "Orders", "OrdersController.cs"), """
            namespace Shop.Api;

            public sealed class OrdersController(Shop.Orders.OrderService service)
            {
                public Task<Guid> Post(Shop.Orders.Order order) => service.SubmitAsync(order, default);
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(Root, "docs", "architecture.md"), "# Architecture\n\n## Ordering\n\nOrders are submitted via OrderService.SubmitAsync, never from the controller directly.\n");
        await File.WriteAllTextAsync(Path.Combine(Root, "bin", "ignored.cs"), "public class ShouldNotBeIndexed {}");
        await File.WriteAllTextAsync(Path.Combine(Root, ".env"), "SECRET_TOKEN=abcdefghijklmnop");

        HasGit = await Git("init", "-q") && await Git("add", "-A") && await Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "initial");
        if (HasGit)
        {
            // Second commit touching two files together, for co-change analysis.
            await File.AppendAllTextAsync(Path.Combine(Root, "src", "Orders", "OrderService.cs"), "\n// touched\n");
            await File.AppendAllTextAsync(Path.Combine(Root, "src", "Orders", "OrdersController.cs"), "\n// touched\n");
            await Git("add", "-A");
            await Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "touch both");
        }

        var environment = McpServices.Hosting.ProcessOutput.GitBackgroundHelpersOff.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        Server = await ServerFixture.StartAsync(
            "McpServices.Index",
            ["--root", Root, "--store", $"sqlite:{StorePath}", "--log-level", "Warning"],
            environment);
    }

    public async Task<bool> Git(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            // Otherwise git may leave an fsmonitor daemon behind that inherits the test host's pipes
            // and keeps vstest waiting for end-of-file after all tests finished (Windows).
            foreach (var (key, value) in McpServices.Hosting.ProcessOutput.GitBackgroundHelpersOff)
            {
                info.Environment[key] = value;
            }

            using var process = Process.Start(info)!;
            process.StandardInput.Close();
            var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync();
            try
            {
                await drain.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Pipes held open by an orphaned helper; the exit code is all we need.
            }

            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(StorePath)!, Path.GetFileName(StorePath) + "*"))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
    }
}

[Collection("index-server")]
public class IndexServerTests(IndexServerFixture fixture) : IClassFixture<IndexServerFixture>
{
    private ServerFixture Server => fixture.Server;

    [Fact]
    public async Task Exposes_all_tools()
    {
        var tools = await Server.ToolNamesAsync();
        Assert.Equal(
            ["find_related_files", "forget", "forget_repository", "get_file_outline", "get_symbol", "index_repository", "index_status", "list_repositories", "mark_useful", "recall", "reindex", "remember", "search_code", "search_symbols", "server_info", "verify_index"],
            tools);
    }

    [Fact]
    public async Task Indexes_incrementally_and_skips_ignored_files()
    {
        var first = await Server.CallJsonAsync("index_repository");
        var run = first.GetProperty("run");
        Assert.True(run.GetProperty("completed").GetBoolean());
        Assert.True(run.GetProperty("added").GetInt32() + run.GetProperty("unchanged").GetInt32() >= 3);

        var status = first.GetProperty("status");
        Assert.True(status.GetProperty("indexed").GetBoolean());
        Assert.True(status.GetProperty("symbolCount").GetInt32() >= 8);
        Assert.False(status.GetProperty("freshness").GetProperty("stale").GetBoolean());

        var second = await Server.CallJsonAsync("index_repository");
        Assert.Equal(0, second.GetProperty("run").GetProperty("added").GetInt32());
        Assert.True(second.GetProperty("run").GetProperty("unchanged").GetInt32() >= 3);

        var ignored = await Server.CallJsonAsync("search_symbols", new { query = "ShouldNotBeIndexed" });
        Assert.Equal(0, ignored.GetProperty("total").GetInt32());
        var secret = await Server.CallJsonAsync("search_code", new { query = "SECRET_TOKEN" });
        Assert.Equal(0, secret.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Search_code_finds_symbols_and_text_and_accepts_feedback()
    {
        await Server.CallJsonAsync("index_repository");
        var result = await Server.CallJsonAsync("search_code", new { query = "where are orders submitted" });
        Assert.True(result.GetProperty("total").GetInt32() > 0);
        var hits = result.GetProperty("hits").EnumerateArray().ToList();
        Assert.Contains(hits, h => h.GetProperty("path").GetString() == "src/Orders/OrderService.cs");
        Assert.Contains(hits, h => h.GetProperty("path").GetString() == "docs/architecture.md");
        Assert.Contains(hits, h => h.GetProperty("type").GetString() == "symbol" && h.GetProperty("title").GetString() == "Shop.Orders.OrderService.SubmitAsync");

        var queryId = result.GetProperty("queryId").GetString()!;
        var docHit = hits.First(h => h.GetProperty("path").GetString() == "docs/architecture.md");
        var feedback = await Server.CallJsonAsync("mark_useful", new { queryId, hits = new[] { docHit.GetProperty("id").GetString() } });
        Assert.True(feedback.GetProperty("feedbackRecorded").GetInt32() >= 1);

        var again = await Server.CallJsonAsync("search_code", new { query = "orders submitted" });
        var boosted = again.GetProperty("hits").EnumerateArray().First(h => h.GetProperty("path").GetString() == "docs/architecture.md");
        var other = again.GetProperty("hits").EnumerateArray().First(h => h.GetProperty("path").GetString() != "docs/architecture.md");
        Assert.True(boosted.GetProperty("score").GetDouble() > 0);
        Assert.NotEqual(boosted.GetProperty("score").GetDouble(), other.GetProperty("score").GetDouble());

        var expired = await Server.CallExpectingErrorAsync("mark_useful", new { queryId = "nope", hits = new[] { "x" } });
        Assert.Contains("Unknown or expired", expired, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symbol_lookups_and_outline_work()
    {
        await Server.CallJsonAsync("index_repository");

        var symbols = await Server.CallJsonAsync("search_symbols", new { query = "Submit" });
        Assert.Contains(symbols.GetProperty("symbols").EnumerateArray(), s => s.GetProperty("fullName").GetString() == "Shop.Orders.OrderService.SubmitAsync");

        var classes = await Server.CallJsonAsync("search_symbols", new { query = "Order", kind = "class" });
        Assert.All(classes.GetProperty("symbols").EnumerateArray(), s => Assert.Equal("class", s.GetProperty("kind").GetString()));

        var symbol = await Server.CallJsonAsync("get_symbol", new { name = "Shop.Orders.OrderService.SubmitAsync" });
        var declaration = Assert.Single(symbol.GetProperty("symbols").EnumerateArray());
        Assert.Contains("repository.SaveAsync", declaration.GetProperty("source").GetString(), StringComparison.Ordinal);
        Assert.Equal("Submit an order; this is the single entry point for ordering.", declaration.GetProperty("doc").GetString());

        var outline = await Server.CallJsonAsync("get_file_outline", new { path = "src/Orders/OrderService.cs" });
        var names = outline.GetProperty("symbols").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Equal(["OrderService", "SubmitAsync", "PendingCount", "IOrderRepository", "SaveAsync", "Order"], names);

        Assert.Contains("not in the index", await Server.CallExpectingErrorAsync("get_file_outline", new { path = "nope.cs" }), StringComparison.Ordinal);
        Assert.Contains("No symbol named", await Server.CallExpectingErrorAsync("get_symbol", new { name = "DoesNotExist" }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notes_are_recalled_and_flagged_stale_when_files_change()
    {
        await Server.CallJsonAsync("index_repository");
        var file = Path.Combine(fixture.Root, "src", "Orders", "OrdersController.cs");
        var original = await File.ReadAllTextAsync(file);
        try
        {
            var remembered = await Server.CallJsonAsync("remember", new
            {
                note = "Ordering always goes through OrderService.SubmitAsync; the controller must not call the repository. Password=hunter2 should vanish.",
                files = new[] { "src/Orders/OrdersController.cs" },
                tags = new[] { "Architecture" },
            });
            var id = remembered.GetProperty("id").GetInt64();
            Assert.True(remembered.GetProperty("redacted").GetBoolean());

            var recalled = await Server.CallJsonAsync("recall", new { query = "controller repository" });
            var note = recalled.GetProperty("notes").EnumerateArray().Single(n => n.GetProperty("id").GetInt64() == id);
            Assert.DoesNotContain("hunter2", note.GetProperty("note").GetString(), StringComparison.Ordinal);
            Assert.False(note.GetProperty("possiblyStale").GetBoolean());
            Assert.Equal("architecture", note.GetProperty("tags")[0].GetString());

            await File.WriteAllTextAsync(file, original + "\n// changed after the note\n");
            await Server.CallJsonAsync("index_repository");

            var stale = await Server.CallJsonAsync("recall", new { tag = "architecture" });
            var staleNote = stale.GetProperty("notes").EnumerateArray().Single(n => n.GetProperty("id").GetInt64() == id);
            Assert.True(staleNote.GetProperty("possiblyStale").GetBoolean());
            Assert.Equal("src/Orders/OrdersController.cs", staleNote.GetProperty("changedFiles")[0].GetString());

            var marked = await Server.CallJsonAsync("mark_useful", new { noteIds = new[] { id } });
            Assert.Equal(1, marked.GetProperty("notesMarked").GetInt32());

            var forgotten = await Server.CallJsonAsync("forget", new { id });
            Assert.True(forgotten.GetProperty("deleted").GetBoolean());
        }
        finally
        {
            await File.WriteAllTextAsync(file, original);
            await Server.CallJsonAsync("index_repository");
        }
    }

    [Fact]
    public async Task Stale_index_is_detected_and_refreshed_inline()
    {
        await Server.CallJsonAsync("index_repository");
        var newFile = Path.Combine(fixture.Root, "src", "Orders", "Discounts.cs");
        try
        {
            await File.WriteAllTextAsync(newFile, "namespace Shop.Orders;\npublic static class DiscountCalculator { public static decimal Apply(decimal total) => total * 0.9m; }\n");

            var status = await Server.CallJsonAsync("index_status");
            var freshness = status.GetProperty("freshness");
            Assert.True(freshness.GetProperty("stale").GetBoolean());
            Assert.Contains(freshness.GetProperty("changedFiles").EnumerateArray(), f => f.GetString() == "src/Orders/Discounts.cs");

            // search_code refreshes small deltas inline before answering.
            var search = await Server.CallJsonAsync("search_symbols", new { query = "DiscountCalculator" });
            Assert.Contains(search.GetProperty("symbols").EnumerateArray(), s => s.GetProperty("fullName").GetString() == "Shop.Orders.DiscountCalculator");
            Assert.Equal("refreshed inline", search.GetProperty("freshness").GetProperty("refreshAction").GetString());
            Assert.False(search.GetProperty("freshness").GetProperty("stale").GetBoolean());
        }
        finally
        {
            File.Delete(newFile);
            await Server.CallJsonAsync("index_repository");
        }

        var after = await Server.CallJsonAsync("search_symbols", new { query = "DiscountCalculator" });
        Assert.Equal(0, after.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Verify_reports_healthy_index_and_repairs_mismatches()
    {
        await Server.CallJsonAsync("index_repository");
        var healthy = await Server.CallJsonAsync("verify_index");
        Assert.True(healthy.GetProperty("healthy").GetBoolean());
        Assert.Equal("ok", healthy.GetProperty("fullTextIndex").GetString());

        var file = Path.Combine(fixture.Root, "docs", "architecture.md");
        var original = await File.ReadAllTextAsync(file);
        try
        {
            await File.WriteAllTextAsync(file, original + "\nExtra paragraph.\n");
            var report = await Server.CallJsonAsync("verify_index", new { repair = true });
            Assert.Contains("docs/architecture.md", report.GetProperty("mismatched").EnumerateArray().Select(m => m.GetString()));
            Assert.True(report.GetProperty("repaired").GetProperty("run").GetProperty("completed").GetBoolean());

            var again = await Server.CallJsonAsync("verify_index");
            Assert.True(again.GetProperty("healthy").GetBoolean());
        }
        finally
        {
            await File.WriteAllTextAsync(file, original);
            await Server.CallJsonAsync("index_repository");
        }
    }

    [Fact]
    public async Task Related_files_come_from_git_history()
    {
        if (!fixture.HasGit)
        {
            return;
        }

        var result = await Server.CallJsonAsync("find_related_files", new { path = "src/Orders/OrderService.cs" });
        Assert.True(result.GetProperty("commitsAnalyzed").GetInt32() >= 1);
        var related = Assert.Single(result.GetProperty("related").EnumerateArray(), r => r.GetProperty("path").GetString() == "src/Orders/OrdersController.cs");
        Assert.True(related.GetProperty("coChanges").GetInt32() >= 1);
    }

    [Fact]
    public async Task Status_resource_and_repository_listing()
    {
        await Server.CallJsonAsync("index_repository");
        var list = await Server.CallJsonAsync("list_repositories");
        var repo = Assert.Single(list.GetProperty("repositories").EnumerateArray());
        var repoId = repo.GetProperty("repoId").GetString()!;
        Assert.Equal("sqlite", list.GetProperty("store").GetProperty("kind").GetString());

        var resource = await Server.Client.ReadResourceAsync($"index://{repoId}/status");
        var text = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents));
        using var doc = JsonDocument.Parse(text.Text);
        Assert.Equal(repoId, doc.RootElement.GetProperty("repoId").GetString());
        Assert.True(doc.RootElement.GetProperty("indexed").GetBoolean());
    }
}
