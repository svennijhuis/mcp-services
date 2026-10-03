using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpServices.Omni;
using McpServices.TestSupport;

namespace McpServices.Omni.Tests;

public sealed class OmniServerTests : IClassFixture<OmniFixture>
{
    private readonly OmniFixture _fixture;

    public OmniServerTests(OmniFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exposes_the_four_gateway_tools()
    {
        var names = await _fixture.Server.ToolNamesAsync();
        Assert.Equal(["discover_servers", "discover_tools", "get_tool_schema", "invoke_tool", "server_info"], names);
        Assert.DoesNotContain("read_text_file", names);
        Assert.DoesNotContain("read_query", names);
        Assert.DoesNotContain("load_solution", names);
    }

    [Fact]
    public async Task Discover_servers_lists_enabled_only()
    {
        var before = _fixture.Fake.RequestCount;
        var raw = await _fixture.Server.CallTextAsync("discover_servers");
        var result = JsonDocument.Parse(raw).RootElement;
        var ids = result.GetProperty("servers").EnumerateArray().Select(server => server.GetProperty("id").GetString()).ToList();
        Assert.Equal(["demo"], ids);
        var demo = result.GetProperty("servers")[0];
        Assert.Equal(["id", "summary", "title"], demo.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("http://", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("off", raw, StringComparison.Ordinal);
        Assert.Equal(before, _fixture.Fake.RequestCount);
    }

    [Fact]
    public async Task Disabled_id_does_not_connect()
    {
        var before = _fixture.Fake.RequestCount;
        var error = await _fixture.Server.CallExpectingErrorAsync("discover_tools", new { server = "off" });
        Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("401", error, StringComparison.Ordinal);
        Assert.Equal(before, _fixture.Fake.RequestCount);

        error = await _fixture.Server.CallExpectingErrorAsync("invoke_tool", new { server = "off", tool = "echo" });
        Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, _fixture.Fake.RequestCount);
    }

    [Fact]
    public async Task Unknown_id_errors()
    {
        var before = _fixture.Fake.RequestCount;
        var error = await _fixture.Server.CallExpectingErrorAsync("get_tool_schema", new { server = "missing", tool = "echo" });
        Assert.Contains("Unknown server", error, StringComparison.Ordinal);
        Assert.DoesNotContain("401", error, StringComparison.Ordinal);
        Assert.Equal(before, _fixture.Fake.RequestCount);
    }

    [Fact]
    public async Task Url_argument_rejected()
    {
        var before = _fixture.Fake.RequestCount;
        var error = await _fixture.Server.CallExpectingErrorAsync("discover_tools", new { server = "demo", url = "http://127.0.0.1:9/mcp" });
        Assert.Contains("URL is not accepted", error, StringComparison.Ordinal);
        Assert.Equal(before, _fixture.Fake.RequestCount);

        error = await _fixture.Server.CallExpectingErrorAsync("invoke_tool", new { server = "http://127.0.0.1:9/mcp", tool = "echo" });
        Assert.Contains("URL is not accepted", error, StringComparison.Ordinal);
        Assert.Equal(before, _fixture.Fake.RequestCount);
    }

    [Fact]
    public async Task Schema_response_is_one_tool()
    {
        var raw = await _fixture.Server.CallTextAsync("get_tool_schema", new { server = "demo", tool = "echo" });
        var schema = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(["description", "inputSchema", "name"], schema.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("echo", schema.GetProperty("name").GetString());
        Assert.Equal("Echo text\nsecond line", schema.GetProperty("description").GetString());
        Assert.Equal("object", schema.GetProperty("inputSchema").GetProperty("type").GetString());
        Assert.True(schema.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("text", out _));
        Assert.DoesNotContain("secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"other\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_tools_is_names_and_one_line()
    {
        var listed = await _fixture.Server.CallJsonAsync("discover_tools", new { server = "demo" });
        var tools = listed.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal("echo", tools[0].GetProperty("name").GetString());
        Assert.Equal("other", tools[1].GetProperty("name").GetString());
        var echo = tools[0];
        Assert.Equal("Echo text", echo.GetProperty("description").GetString());
        Assert.False(echo.TryGetProperty("inputSchema", out _));
        Assert.DoesNotContain("inputSchema", listed.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_truncates_at_max_chars()
    {
        var text = await _fixture.Server.CallTextAsync("invoke_tool", new { server = "demo", tool = "echo", arguments = new { text = "hello", href = "http://example.com/a" } });
        Assert.Equal(16, text.Length);
        Assert.Equal(new string('x', 16), text);
        var body = _fixture.Fake.Bodies[^1];
        Assert.Contains("hello", body, StringComparison.Ordinal);
        Assert.Contains("http://example.com/a", body, StringComparison.Ordinal);
        Assert.All(_fixture.Fake.AuthorizationHeaders, header => Assert.True(string.IsNullOrEmpty(header)));
    }

    [Fact]
    public async Task Initializes_once_with_protocol_2025_06_18()
    {
        var before = _fixture.Fake.Count("initialize");
        await _fixture.Server.CallJsonAsync("discover_tools", new { server = "demo" });
        await _fixture.Server.CallJsonAsync("get_tool_schema", new { server = "demo", tool = "echo" });
        Assert.Equal(before == 0 ? 1 : before, _fixture.Fake.Count("initialize"));
        Assert.Contains("2025-06-18", _fixture.Fake.ProtocolVersions, StringComparer.Ordinal);
        Assert.Contains("tools/list", _fixture.Fake.RpcMethods, StringComparer.Ordinal);
    }

    [Fact]
    public void Checked_in_registry_is_disabled_compose_dns()
    {
        var registry = OmniRegistry.Load(RepoFile("src/servers/McpServices.Omni/registry.json"));
        Assert.Equal(["database", "filesystem", "index", "learnings", "roslyn"], registry.Servers.Select(server => server.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.All(registry.Servers, server =>
        {
            Assert.False(server.Enabled);
            Assert.Equal(8000, server.MaxChars);
            Assert.Equal("summary", server.Projection);
            Assert.False(string.IsNullOrWhiteSpace(server.Role));
            Assert.Equal($"http://{server.Id}:5100/mcp", server.BaseUrl.ToString());
            Assert.Equal($"http://{server.Id}:5100/healthz", server.HealthUrl.ToString());
        });
        Assert.Empty(registry.Enabled);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpServices.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not find the repository root from the test output.");
        }

        return Path.Combine(dir.FullName, relative);
    }
}

public sealed class OmniDownTests : IAsyncLifetime
{
    private int _port;
    private string _registry = null!;
    private ServerFixture _server = null!;

    public async Task InitializeAsync()
    {
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            _port = ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        _registry = Path.Combine(Path.GetTempPath(), "omni-down-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(_registry, BackendRegistry("down", _port, enabled: true, maxChars: 8000));
        _server = await ServerFixture.StartAsync("McpServices.Omni", ["--registry", _registry, "--log-level", "Warning"]);
    }

    [Fact]
    public async Task Down_server_is_a_tool_error()
    {
        var error = await _server.CallExpectingErrorAsync("discover_tools", new { server = "down" });
        Assert.Contains("unreachable", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("401", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Socket", error, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        File.Delete(_registry);
    }

    internal static string BackendRegistry(string id, int port, bool enabled, int maxChars) =>
        $$"""
        {
          "servers": [
            {
              "id": "{{id}}",
              "title": "Backend",
              "summary": "Test backend.",
              "enabled": {{(enabled ? "true" : "false")}},
              "role": "test",
              "baseUrl": "http://127.0.0.1:{{port}}/mcp",
              "healthUrl": "http://127.0.0.1:{{port}}/healthz",
              "projection": "summary",
              "maxChars": {{maxChars}}
            }
          ]
        }
        """;
}

public sealed class OmniTimeoutTests : IAsyncLifetime
{
    private readonly HangMcp _hang = new();
    private string _registry = null!;
    private ServerFixture _server = null!;

    public async Task InitializeAsync()
    {
        _registry = Path.Combine(Path.GetTempPath(), "omni-timeout-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(_registry, OmniDownTests.BackendRegistry("slow", _hang.Port, enabled: true, maxChars: 8000));
        _server = await ServerFixture.StartAsync("McpServices.Omni", ["--registry", _registry, "--timeout", "1", "--log-level", "Warning"]);
    }

    [Fact]
    public async Task Timeout_is_a_tool_error()
    {
        var error = await _server.CallExpectingErrorAsync("invoke_tool", new { server = "slow", tool = "echo" });
        Assert.Contains("timed out", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("401", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Socket", error, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _hang.Dispose();
        File.Delete(_registry);
    }
}

public sealed class OmniFixture : IAsyncLifetime
{
    private string _registry = null!;

    public FakeMcp Fake { get; private set; } = null!;

    public ServerFixture Server { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Fake = new FakeMcp();
        _registry = Path.Combine(Path.GetTempPath(), "omni-" + Guid.NewGuid().ToString("N") + ".json");
        var json = $$"""
        {
          "servers": [
            {
              "id": "demo",
              "title": "Demo",
              "summary": "Fake backend for tests.",
              "enabled": true,
              "role": "test",
              "baseUrl": "{{Fake.Endpoint}}",
              "healthUrl": "{{Fake.HealthUrl}}",
              "projection": "summary",
              "maxChars": 16
            },
            {
              "id": "off",
              "title": "Off",
              "summary": "Disabled backend.",
              "enabled": false,
              "role": "test",
              "baseUrl": "{{Fake.Endpoint}}",
              "healthUrl": "{{Fake.HealthUrl}}",
              "projection": "summary",
              "maxChars": 8000
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(_registry, json);
        Server = await ServerFixture.StartAsync("McpServices.Omni", ["--registry", _registry, "--log-level", "Warning"]);
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        Fake.Dispose();
        File.Delete(_registry);
    }
}

public sealed class FakeMcp : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<LoggedRequest> _requests = [];

    public FakeMcp()
    {
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            Port = ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string Endpoint => $"http://127.0.0.1:{Port}/mcp";

    public string HealthUrl => $"http://127.0.0.1:{Port}/healthz";

    public int RequestCount
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    public IReadOnlyList<string> RpcMethods
    {
        get
        {
            lock (_gate)
            {
                return _requests.Select(request => request.RpcMethod ?? string.Empty).ToList();
            }
        }
    }

    public IReadOnlyList<string?> AuthorizationHeaders
    {
        get
        {
            lock (_gate)
            {
                return _requests.Select(request => request.Authorization).ToList();
            }
        }
    }

    public IReadOnlyList<string?> ProtocolVersions
    {
        get
        {
            lock (_gate)
            {
                return _requests.Select(request => request.ProtocolVersion).Where(version => !string.IsNullOrEmpty(version)).ToList()!;
            }
        }
    }

    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_gate)
            {
                return _requests.Select(request => request.Body).ToList();
            }
        }
    }

    public int Count(string rpcMethod)
    {
        lock (_gate)
        {
            return _requests.Count(request => request.RpcMethod == rpcMethod);
        }
    }

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

            string body;
            using (var reader = new StreamReader(context.Request.InputStream))
            {
                body = await reader.ReadToEndAsync();
            }

            string? rpc = null;
            JsonNode? id = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var node = JsonNode.Parse(body) as JsonObject;
                    rpc = node?["method"]?.GetValue<string>();
                    id = node?["id"]?.DeepClone();
                }
                catch (JsonException)
                {
                    rpc = null;
                }
            }

            lock (_gate)
            {
                _requests.Add(new LoggedRequest(
                    context.Request.HttpMethod,
                    rpc,
                    context.Request.Headers["Authorization"],
                    context.Request.Headers["MCP-Protocol-Version"],
                    body));
            }

            if (context.Request.HttpMethod == "GET")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                continue;
            }

            if (context.Request.HttpMethod == "DELETE")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                continue;
            }

            if (rpc is not null && rpc.StartsWith("notifications/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                continue;
            }

            JsonObject payload = rpc switch
            {
                "initialize" => Result(id, new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "0" },
                }),
                "tools/list" => Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        Tool("echo", "Echo text\nsecond line", "text"),
                        Tool("other", "Other tool", "secret"),
                    },
                }),
                "tools/call" => Result(id, new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = new string('x', 40) },
                    },
                    ["isError"] = false,
                }),
                _ => new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" },
                },
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.Headers["Mcp-Session-Id"] = "sess-test";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Tool(string name, string description, string property) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [property] = new JsonObject { ["type"] = "string" },
            },
        },
    };

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }

    private sealed record LoggedRequest(string HttpMethod, string? RpcMethod, string? Authorization, string? ProtocolVersion, string Body);
}

/// <summary>Accepts a connection and never answers, so the client hits its timeout.</summary>
public sealed class HangMcp : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public HangMcp()
    {
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            Port = ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(HoldAsync);
    }

    public int Port { get; }

    private async Task HoldAsync()
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

            try
            {
                await Task.Delay(Timeout.Infinite, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    context.Response.Abort();
                }
                catch (Exception)
                {
                    // The client already gave up.
                }

                return;
            }
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
