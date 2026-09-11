using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace McpServices.Hosting.Tests;

public class HttpHostServerTests
{
    [Fact]
    public async Task Loopback_handshake_lists_tools_and_server_info()
    {
        await using var server = await HttpTestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "server_info");

        var result = await client.CallToolAsync("server_info");
        Assert.NotEqual(true, result.IsError);
        var json = JsonDocument.Parse(Text(result));
        Assert.Equal("mcp-host-test", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("streamable-http", json.RootElement.GetProperty("transport").GetString());
        Assert.Equal("http://127.0.0.1:" + server.BaseAddress.Port + "/mcp", json.RootElement.GetProperty("httpEndpoint").GetString());
        Assert.False(json.RootElement.GetProperty("configuration").GetProperty("authRequired").GetBoolean());
        Assert.DoesNotContain("authToken", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Token_required_returns_401_without_header_and_connects_with_bearer()
    {
        const string token = "test-token-value";
        await using var server = await HttpTestServer.StartAsync("--auth-token", token);

        using var unauthorized = await server.Http.PostAsync(
            server.McpUri,
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("Bearer", unauthorized.Headers.WwwAuthenticate.ToString());

        using var health = await server.Http.GetAsync(new Uri(server.BaseAddress, "health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var healthz = await server.Http.GetAsync(new Uri(server.BaseAddress, "healthz"));
        Assert.Equal(HttpStatusCode.OK, healthz.StatusCode);

        await using var client = await server.ConnectAsync(token);
        var info = await client.CallToolAsync("server_info");
        var json = JsonDocument.Parse(Text(info));
        Assert.True(json.RootElement.GetProperty("configuration").GetProperty("authRequired").GetBoolean());
        Assert.DoesNotContain(token, json.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_accepts_json_and_event_stream()
    {
        await using var server = await HttpTestServer.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, server.McpUri)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await server.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("mcp-host-test", body, StringComparison.Ordinal);
        Assert.Contains("protocolVersion", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cors_preflight_when_configured_and_absent_otherwise()
    {
        await using var withCors = await HttpTestServer.StartAsync("--cors-origin", "https://grok.com");
        using var preflight = new HttpRequestMessage(HttpMethod.Options, withCors.McpUri);
        preflight.Headers.Add("Origin", "https://grok.com");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,authorization,mcp-protocol-version");
        using var allowed = await withCors.Http.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal("https://grok.com", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var allowHeaders = string.Join(",", allowed.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("authorization", allowHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mcp-protocol-version", allowHeaders, StringComparison.OrdinalIgnoreCase);

        await using var withoutCors = await HttpTestServer.StartAsync();
        using var blocked = new HttpRequestMessage(HttpMethod.Options, withoutCors.McpUri);
        blocked.Headers.Add("Origin", "https://grok.com");
        blocked.Headers.Add("Access-Control-Request-Method", "POST");
        using var response = await withoutCors.Http.SendAsync(blocked);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Non_loopback_without_token_exits_2()
    {
        var previous = Environment.GetEnvironmentVariable("MCP_AUTH_TOKEN");
        Environment.SetEnvironmentVariable("MCP_AUTH_TOKEN", null);
        try
        {
            var descriptor = new ServerDescriptor("mcp-host-test", "HTTP host tests");
            var code = await McpServerHost.RunAsync(
                ["--http", "--host", "0.0.0.0", "--port", "59999"],
                descriptor,
                _ => { },
                CancellationToken.None);
            Assert.Equal(2, code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_AUTH_TOKEN", previous);
        }
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
}
