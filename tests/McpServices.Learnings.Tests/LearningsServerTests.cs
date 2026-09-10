using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using McpServices.TestSupport;
using ModelContextProtocol.Protocol;

namespace McpServices.Learnings.Tests;

/// <summary>Fake Cursor Cloud Agents API: one 503 first (to prove retries), then a created agent that finishes with a PR.</summary>
public sealed class FakeCursorApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private int _failuresLeft = 1;

    public FakeCursorApi()
    {
        using (var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/";
        }

        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<(string Method, string Path, string? Authorization, string Body)> Requests { get; } = new();

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var path = context.Request.Url!.AbsolutePath;
            Requests.Enqueue((context.Request.HttpMethod, path, context.Request.Headers["Authorization"], body));

            object payload;
            if (context.Request.HttpMethod == "POST" && path == "/v0/agents")
            {
                if (Interlocked.Decrement(ref _failuresLeft) >= 0)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    continue;
                }

                context.Response.StatusCode = 201;
                payload = new { id = "bc_test_123", name = "Learnings agent", status = "CREATING", target = new { url = "https://cursor.com/agents?id=bc_test_123", branchName = "cursor/learnings-1" } };
            }
            else if (context.Request.HttpMethod == "GET" && path.StartsWith("/v0/agents/", StringComparison.Ordinal))
            {
                payload = new { id = path["/v0/agents/".Length..], status = "FINISHED", summary = "Updated the skill docs", target = new { prUrl = "https://github.com/svennijhuis/agentpacks/pull/7", url = "https://cursor.com/agents?id=bc_test_123" } };
            }
            else
            {
                context.Response.StatusCode = 404;
                payload = new { code = "not_found" };
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}

public sealed class LearningsServerFixture : IAsyncLifetime
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mcp-learnings-" + Guid.NewGuid().ToString("N"));

    public FakeCursorApi CursorApi { get; } = new();

    public ServerFixture Server { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDir);
        Server = await ServerFixture.StartAsync(
            "McpServices.Learnings",
            [
                "--repo", "svennijhuis/agentPacks",
                "--target-repo", "svennijhuis/mcp-services",
                "--store", $"sqlite:{Path.Combine(DataDir, "learnings.db")}",
                "--data-dir", DataDir,
                "--dispatch", "manual",
                "--min-corroborations", "3",
                "--max-dispatches-per-day", "2",
                "--cursor-api", CursorApi.BaseUrl,
                "--log-level", "Warning",
            ],
            new Dictionary<string, string?> { ["CURSOR_API_KEY"] = "test-key-abc" });
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        CursorApi.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class LearningsServerTests(LearningsServerFixture fixture) : IClassFixture<LearningsServerFixture>
{
    private ServerFixture Server => fixture.Server;

    [Fact]
    public async Task Exposes_all_tools()
    {
        var tools = await Server.ToolNamesAsync();
        Assert.Equal(
            ["create_proposal", "dispatch_proposal", "export_learnings_md", "forget_learning", "get_dispatch_status", "get_proposal", "get_recommendations", "import_learnings_md", "list_proposals", "mark_learning_useful", "query_learnings", "record_feedback", "record_learning", "server_info", "summarize_period", "update_proposal_status"],
            tools);

        var prompts = await Server.Client.ListPromptsAsync();
        Assert.Contains(prompts, p => p.Name == "improvement_pr");
    }

    [Fact]
    public async Task Server_info_never_exposes_the_api_key()
    {
        var info = await Server.CallJsonAsync("server_info");
        var text = info.GetRawText();
        Assert.DoesNotContain("test-key-abc", text, StringComparison.Ordinal);
        Assert.Contains("\"cursorApiKey\": \"set\"", text, StringComparison.Ordinal);
        Assert.Contains("svennijhuis/agentpacks", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_learnings_are_deduplicated_and_secrets_redacted()
    {
        var first = await Server.CallJsonAsync("record_learning", new { outcome = "worked", title = "Use --no-restore in CI after restore step", detail = "Saves 40s; token ghp_abcdefghijklmnopqrstuvwxyz0123456789 was in the log", category = "ci", tags = new[] { "dotnet" }, toolOrSkill = "dotnet build" });
        Assert.True(first.GetProperty("created").GetBoolean());
        var id = first.GetProperty("learning").GetProperty("id").GetInt64();
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789", first.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("svennijhuis/agentpacks", first.GetProperty("learning").GetProperty("repo").GetString());

        var second = await Server.CallJsonAsync("record_learning", new { outcome = "worked", title = "use --no-restore in CI after restore step.", toolOrSkill = "Dotnet Build" });
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(id, second.GetProperty("learning").GetProperty("id").GetInt64());
        Assert.Equal(2, second.GetProperty("learning").GetProperty("occurrences").GetInt32());
        Assert.Contains("Saves 40s", second.GetProperty("learning").GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_unknown_outcome()
    {
        var error = await Server.CallExpectingErrorAsync("record_learning", new { outcome = "meh", title = "x" });
        Assert.Contains("worked, failed, partial", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recommendations_split_do_and_avoid_and_flag_conflicts()
    {
        await Server.CallJsonAsync("record_learning", new { outcome = "worked", title = "Run the squad verifier before review for API changes", category = "workflow", tags = new[] { "squad", "review" }, toolOrSkill = "/squad" });
        await Server.CallJsonAsync("record_learning", new { outcome = "failed", title = "Skipping the security reviewer on auth changes", detail = "Missed an open redirect in the login flow.", category = "review", tags = new[] { "squad", "security" }, toolOrSkill = "/squad" });
        await Server.CallJsonAsync("record_learning", new { outcome = "worked", title = "Skipping the security reviewer on auth changes", detail = "Fine for a copy change in the login page.", category = "review", tags = new[] { "squad" }, toolOrSkill = "/squad" });

        var result = await Server.CallJsonAsync("get_recommendations", new { task = "review the login flow changes with the squad security reviewer", tags = new[] { "squad" } });

        var avoid = result.GetProperty("avoid").EnumerateArray().ToList();
        var @do = result.GetProperty("do").EnumerateArray().ToList();
        Assert.Contains(avoid, a => a.GetProperty("title").GetString()!.StartsWith("Skipping the security reviewer", StringComparison.Ordinal) && a.GetProperty("conflict").GetBoolean());
        Assert.Contains(@do, d => d.GetProperty("title").GetString()!.StartsWith("Skipping the security reviewer", StringComparison.Ordinal) && d.GetProperty("conflict").GetBoolean());
        Assert.Contains(@do, d => d.GetProperty("title").GetString()!.StartsWith("Run the squad verifier", StringComparison.Ordinal) && !d.GetProperty("conflict").GetBoolean());
        Assert.All(avoid.Concat(@do), r => Assert.InRange(r.GetProperty("confidence").GetDouble(), 0.1, 1.0));

        var useful = await Server.CallJsonAsync("mark_learning_useful", new { learningIds = new[] { avoid[0].GetProperty("id").GetInt64() } });
        Assert.Equal(1, useful.GetProperty("updated").GetInt32());
    }

    [Fact]
    public async Task Query_uses_full_text_and_filters()
    {
        await Server.CallJsonAsync("record_learning", new { outcome = "failed", title = "Playwright flaked on the checkout spec under parallel workers", category = "testing", tags = new[] { "e2e" }, toolOrSkill = "playwright" });

        var hits = await Server.CallJsonAsync("query_learnings", new { query = "flaky checkout playwright", outcome = "failed" });
        Assert.True(hits.GetProperty("total").GetInt32() >= 1);
        Assert.Contains(hits.GetProperty("learnings").EnumerateArray(), l => l.GetProperty("title").GetString()!.Contains("Playwright", StringComparison.Ordinal));

        var none = await Server.CallJsonAsync("query_learnings", new { query = "playwright", outcome = "worked", category = "testing" });
        Assert.Equal(0, none.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Feedback_shows_up_in_period_summary()
    {
        await Server.CallJsonAsync("record_feedback", new { tool = "mcp-index", worked = true });
        await Server.CallJsonAsync("record_feedback", new { tool = "mcp-index", worked = false, note = "stale after branch switch" });
        await Server.CallJsonAsync("record_feedback", new { tool = "mcp-roslyn", worked = false });

        var summary = await Server.CallJsonAsync("summarize_period", new { since = "1d" });
        var feedback = summary.GetProperty("feedback").EnumerateArray().ToList();
        var index = feedback.Single(f => f.GetProperty("tool").GetString() == "mcp-index");
        Assert.Equal(1, index.GetProperty("worked").GetInt32());
        Assert.Equal(1, index.GetProperty("failed").GetInt32());
        Assert.True(summary.GetProperty("learnings").GetProperty("total").GetInt32() >= 0);
    }

    [Fact]
    public async Task Imports_and_exports_squad_learnings_md()
    {
        var repoDir = Path.Combine(fixture.DataDir, "repo-" + Guid.NewGuid().ToString("N"), "docs");
        Directory.CreateDirectory(repoDir);
        var source = Path.Combine(repoDir, "learnings.md");
        await File.WriteAllTextAsync(source, """
            # Learnings

            ## 2026-09-05 — /squad

            - Entrypoint: squad
            - Provider: cursor
            - Model tier: inherit
            - Agents spun: squad-planner, squad-implementer
            - Skipped: squad-security-reviewer — no trust boundary changed
            - Ran: plan, implement
            - Result: pass
            - Handoff themes: None
            - Next tweak: keep inherit; the small rename did not need a planner
            """);

        var imported = await Server.CallJsonAsync("import_learnings_md", new { path = source, repo = "svennijhuis/agentPacks" });
        Assert.Equal(1, imported.GetProperty("parsed").GetInt32());
        Assert.Equal(1, imported.GetProperty("created").GetInt32());

        var again = await Server.CallJsonAsync("import_learnings_md", new { path = source, repo = "svennijhuis/agentPacks" });
        Assert.Equal(0, again.GetProperty("created").GetInt32());
        Assert.Equal(1, again.GetProperty("merged").GetInt32());

        var target = Path.Combine(repoDir, "exported.md");
        var exported = await Server.CallJsonAsync("export_learnings_md", new { path = target, category = "squad" });
        Assert.True(exported.GetProperty("appended").GetInt32() >= 1);
        var text = await File.ReadAllTextAsync(target);
        Assert.Contains("## 2026-09-05 — /squad", text, StringComparison.Ordinal);
        Assert.Contains("- Next tweak: keep inherit; the small rename did not need a planner", text, StringComparison.Ordinal);

        // Appending the same entries again is a no-op (append-only log stays clean).
        var second = await Server.CallJsonAsync("export_learnings_md", new { path = target, category = "squad" });
        Assert.Equal(0, second.GetProperty("appended").GetInt32());
    }

    [Fact]
    public async Task Proposal_lifecycle_with_guardrails_and_cloud_dispatch()
    {
        // One learning with a single occurrence: proposal can be created and dry-run, but not dispatched externally.
        var weak = await Server.CallJsonAsync("record_learning", new { outcome = "failed", title = "Skill docs do not mention the --no-build flag", category = "docs", files = new[] { "plugins/dotnet/skills/test/SKILL.md" } });
        var weakId = weak.GetProperty("learning").GetProperty("id").GetInt64();

        var created = await Server.CallJsonAsync("create_proposal", new { learningIds = new[] { weakId } });
        var proposal = created.GetProperty("proposal");
        var proposalId = proposal.GetProperty("id").GetInt64();
        Assert.Equal("draft", proposal.GetProperty("status").GetString());
        Assert.Equal("svennijhuis/agentpacks", proposal.GetProperty("targetRepo").GetString());
        Assert.Contains($"#{weakId}", proposal.GetProperty("prompt").GetString(), StringComparison.Ordinal);
        Assert.Contains("plugins/dotnet/skills/test/SKILL.md", proposal.GetProperty("prompt").GetString(), StringComparison.Ordinal);
        Assert.Contains("DRAFT pull request", proposal.GetProperty("prompt").GetString(), StringComparison.Ordinal);

        var dry = await Server.CallJsonAsync("dispatch_proposal", new { proposalId, mode = "dry-run" });
        var file = dry.GetProperty("result").GetProperty("externalId").GetString()!;
        Assert.True(File.Exists(file));
        Assert.Contains("## Agent prompt", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        Assert.Equal("draft", dry.GetProperty("proposal").GetProperty("status").GetString());

        var blocked = await Server.CallExpectingErrorAsync("dispatch_proposal", new { proposalId, mode = "cursor-cloud" });
        Assert.Contains("3 are required", blocked, StringComparison.Ordinal);

        var whitelist = await Server.CallExpectingErrorAsync("create_proposal", new { learningIds = new[] { weakId }, targetRepo = "someone/else" });
        Assert.Contains("not whitelisted", whitelist, StringComparison.Ordinal);

        var unknownMode = await Server.CallExpectingErrorAsync("dispatch_proposal", new { proposalId, mode = "carrier-pigeon" });
        Assert.Contains("Unknown dispatch mode", unknownMode, StringComparison.Ordinal);

        // Three corroborations: dispatch goes to the (fake) Cloud Agents API, surviving one 503.
        for (var i = 0; i < 3; i++)
        {
            await Server.CallJsonAsync("record_learning", new { outcome = "failed", title = "Agents forget to run the validator before pushing plugin changes", category = "workflow", toolOrSkill = "agentpacks validate", evidence = $"run {i}" });
        }

        var strong = await Server.CallJsonAsync("create_proposal", new { query = "validator before pushing", outcome = "failed", targetRepo = "https://github.com/svennijhuis/agentPacks" });
        var strongId = strong.GetProperty("proposal").GetProperty("id").GetInt64();
        Assert.True(strong.GetProperty("proposal").GetProperty("corroborations").GetInt32() >= 3);

        var dispatched = await Server.CallJsonAsync("dispatch_proposal", new { proposalId = strongId, mode = "cursor-cloud" });
        Assert.Equal("dispatched", dispatched.GetProperty("proposal").GetProperty("status").GetString());
        Assert.Equal("bc_test_123", dispatched.GetProperty("proposal").GetProperty("externalId").GetString());

        var post = fixture.CursorApi.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(2, post.Count); // one 503 + one success
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test-key-abc:")), post[^1].Authorization);
        using (var body = JsonDocument.Parse(post[^1].Body))
        {
            Assert.Equal("https://github.com/svennijhuis/agentpacks", body.RootElement.GetProperty("source").GetProperty("repository").GetString());
            Assert.Equal("main", body.RootElement.GetProperty("source").GetProperty("ref").GetString());
            Assert.True(body.RootElement.GetProperty("target").GetProperty("autoCreatePr").GetBoolean());
            Assert.Contains("Agents forget to run the validator", body.RootElement.GetProperty("prompt").GetProperty("text").GetString(), StringComparison.Ordinal);
        }

        var status = await Server.CallJsonAsync("get_dispatch_status", new { proposalId = strongId });
        Assert.Equal("prOpened", status.GetProperty("status").GetString());
        Assert.Equal("https://github.com/svennijhuis/agentpacks/pull/7", status.GetProperty("prUrl").GetString());
        Assert.Equal("FINISHED", status.GetProperty("remote").GetProperty("remoteStatus").GetString());

        var redispatch = await Server.CallExpectingErrorAsync("dispatch_proposal", new { proposalId = strongId, mode = "cursor-cloud" });
        Assert.Contains("only draft or failed", redispatch, StringComparison.Ordinal);

        var listed = await Server.CallJsonAsync("list_proposals", new { status = "pr_opened" });
        Assert.Contains(listed.GetProperty("proposals").EnumerateArray(), p => p.GetProperty("id").GetInt64() == strongId);

        var manual = await Server.CallJsonAsync("update_proposal_status", new { proposalId = strongId, status = "merged" });
        Assert.Equal("merged", manual.GetProperty("proposal").GetProperty("status").GetString());

        var resource = await Server.Client.ReadResourceAsync($"learnings://proposals/{strongId}");
        var markdown = Assert.IsType<TextResourceContents>(resource.Contents[0]).Text;
        Assert.Contains($"# Proposal #{strongId}", markdown, StringComparison.Ordinal);
        Assert.Contains("https://github.com/svennijhuis/agentpacks/pull/7", markdown, StringComparison.Ordinal);

        var prompt = await Server.Client.GetPromptAsync("improvement_pr", new Dictionary<string, object?> { ["proposalId"] = strongId });
        Assert.Contains(prompt.Messages, m => (m.Content as TextContentBlock)?.Text.Contains("## Acceptance criteria", StringComparison.Ordinal) == true);
    }
}
