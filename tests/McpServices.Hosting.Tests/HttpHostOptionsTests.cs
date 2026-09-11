namespace McpServices.Hosting.Tests;

public class HttpHostOptionsTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void Loopback_without_token_is_unauthenticated()
    {
        var options = HttpHostOptions.Parse(CommandLine.Parse([]), NoEnv);
        Assert.Equal("127.0.0.1", options.Bind);
        Assert.Equal(5100, options.Port);
        Assert.False(options.AuthRequired);
        Assert.Equal("http://127.0.0.1:5100/mcp", options.AdvertisedMcpEndpoint);
    }

    [Fact]
    public void Non_loopback_without_token_fails_closed()
    {
        var ex = Assert.Throws<ServerStartupException>(() =>
            HttpHostOptions.Parse(CommandLine.Parse(["--host", "0.0.0.0"]), NoEnv));
        Assert.Contains("MCP_AUTH_TOKEN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_loopback_with_token_is_allowed()
    {
        var options = HttpHostOptions.Parse(CommandLine.Parse(["--host", "0.0.0.0", "--auth-token", "secret"]), NoEnv);
        Assert.True(options.AuthRequired);
        Assert.Equal("0.0.0.0", options.Bind);
        Assert.Equal("http://0.0.0.0:5100/mcp", options.AdvertisedMcpEndpoint);
    }

    [Fact]
    public void Ipv6_loopback_is_loopback()
    {
        Assert.True(HttpHostOptions.IsLoopbackBind("::1"));
        Assert.True(HttpHostOptions.IsLoopbackBind("[::1]"));
        Assert.True(HttpHostOptions.IsLoopbackBind("localhost"));
        Assert.False(HttpHostOptions.IsLoopbackBind("0.0.0.0"));
        Assert.False(HttpHostOptions.IsLoopbackBind("::"));
    }

    [Fact]
    public void Public_url_is_advertised_and_added_to_allowed_hosts()
    {
        var options = HttpHostOptions.Parse(
            CommandLine.Parse(["--auth-token", "t", "--host", "0.0.0.0", "--public-url", "https://abc.trycloudflare.com"]),
            NoEnv);
        Assert.Equal("https://abc.trycloudflare.com/mcp", options.AdvertisedMcpEndpoint);
        Assert.Contains("abc.trycloudflare.com", options.AllowedHosts);
        Assert.Equal("https://abc.trycloudflare.com", options.PublicUrl);

        var withPath = HttpHostOptions.Parse(
            CommandLine.Parse(["--public-url", "https://abc.trycloudflare.com/mcp"]),
            NoEnv);
        Assert.Equal("https://abc.trycloudflare.com/mcp", withPath.AdvertisedMcpEndpoint);
    }

    [Fact]
    public void Allowed_host_star_disables_filtering()
    {
        var options = HttpHostOptions.Parse(
            CommandLine.Parse(["--auth-token", "t", "--host", "0.0.0.0", "--allowed-host", "*"]),
            NoEnv);
        Assert.Equal("*", options.AllowedHostsSetting);
    }

    [Fact]
    public void Cors_origins_merge_cli_and_env()
    {
        var options = HttpHostOptions.Parse(
            CommandLine.Parse(["--cors-origin", "https://a.example"]),
            name => name == "MCP_CORS_ORIGINS" ? "https://b.example,https://c.example" : null);
        Assert.Equal(["https://a.example", "https://b.example", "https://c.example"], options.CorsOrigins);
        Assert.True(options.CorsEnabled);
    }

    [Fact]
    public void Auth_token_flag_without_value_fails()
    {
        var ex = Assert.Throws<ServerStartupException>(() =>
            HttpHostOptions.Parse(CommandLine.Parse(["--auth-token"]), NoEnv));
        Assert.Contains("requires a value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_env_token_is_missing()
    {
        Assert.Throws<ServerStartupException>(() =>
            HttpHostOptions.Parse(CommandLine.Parse(["--host", "0.0.0.0"]), name => name == "MCP_AUTH_TOKEN" ? "  " : null));
    }

    [Fact]
    public void Cli_token_wins_over_environment()
    {
        var options = HttpHostOptions.Parse(
            CommandLine.Parse(["--auth-token", "from-cli"]),
            name => name == "MCP_AUTH_TOKEN" ? "from-env" : null);
        Assert.Equal("from-cli", options.AuthToken);
    }

    [Fact]
    public void Invalid_public_url_fails()
    {
        Assert.Throws<ServerStartupException>(() =>
            HttpHostOptions.Parse(CommandLine.Parse(["--public-url", "not-a-url"]), NoEnv));
    }

    [Fact]
    public void Invalid_port_fails()
    {
        Assert.Throws<ServerStartupException>(() =>
            HttpHostOptions.Parse(CommandLine.Parse(["--port", "99999"]), NoEnv));
    }

    [Fact]
    public void Ipv6_listen_url_is_bracketed()
    {
        Assert.Equal("http://[::1]:5100", $"http://{HttpHostOptions.FormatBind("::1")}:5100");
    }
}

public class BearerAuthenticationTests
{
    [Fact]
    public void Accepts_bearer_header_case_insensitively()
    {
        Assert.True(BearerAuthentication.Matches("Bearer secret", "secret"));
        Assert.True(BearerAuthentication.Matches("bearer secret", "secret"));
        Assert.False(BearerAuthentication.Matches("Bearer other", "secret"));
        Assert.False(BearerAuthentication.Matches("Basic secret", "secret"));
        Assert.False(BearerAuthentication.Matches(null, "secret"));
        Assert.False(BearerAuthentication.Matches("Bearer ", "secret"));
    }
}
