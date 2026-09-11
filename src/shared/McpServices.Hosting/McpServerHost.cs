using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServices.Hosting;

/// <summary>
/// Boots an MCP server over stdio (default) or Streamable HTTP (<c>--http</c>) from a single binary.
/// All logging goes to stderr so stdout stays reserved for the protocol stream.
/// </summary>
public static class McpServerHost
{
    public const string DefaultHttpPath = "/mcp";

    private static readonly string[] SharedFlags = ["http", "help", "version"];

    /// <summary>Shared transport options every server understands.</summary>
    public static string SharedUsage => """
        Transport options:
          (default)              stdio transport; logs go to stderr
          --http                 Streamable HTTP transport (ASP.NET Core)
          --port <n>             HTTP port (default 5100; env MCP_PORT)
          --host <name>          HTTP bind address (default 127.0.0.1; use 0.0.0.0 in Docker)
          --auth-token <token>   Require Authorization: Bearer on /mcp (env MCP_AUTH_TOKEN).
                                 Mandatory when --host is not loopback. Mandatory for any public URL
                                 (tunnel to loopback included); loopback without a token stays local-only.
          --public-url <url>     Advertised https:// origin (tunnel or reverse proxy); env MCP_PUBLIC_URL
          --allowed-host <name>  Extra Host header values (repeatable; env MCP_ALLOWED_HOSTS, comma/semicolon).
                                 Default: localhost, 127.0.0.1, ::1, the bind address, and the public-url host.
                                 Pass * to disable host filtering (typical for throwaway tunnels).
          --cors-origin <origin> Opt-in browser CORS (repeatable; env MCP_CORS_ORIGINS). Off by default.
          --log-level <level>    Trace|Debug|Information|Warning|Error (default Information)
          --help                 Show help
          --version              Show version
        """;

    public static async Task<int> RunAsync(string[] args, ServerDescriptor descriptor, Action<HostContext> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(configure);

        var commandLine = CommandLine.Parse(args, [.. SharedFlags, .. descriptor.Flags]);
        if (commandLine.HasFlag("version"))
        {
            Console.Out.WriteLine($"{descriptor.Name} {descriptor.Version}");
            return 0;
        }

        if (commandLine.HasFlag("help"))
        {
            PrintHelp(descriptor);
            return 0;
        }

        var useHttp = commandLine.HasFlag("http");
        var logLevel = ParseLogLevel(commandLine.GetOption("log-level", "MCP_LOG_LEVEL"));

        try
        {
            return useHttp
                ? await RunHttpAsync(args, commandLine, descriptor, configure, logLevel, cancellationToken).ConfigureAwait(false)
                : await RunStdioAsync(args, commandLine, descriptor, configure, logLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerStartupException ex)
        {
            await Console.Error.WriteLineAsync($"{descriptor.Name}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    private static async Task<int> RunStdioAsync(string[] args, CommandLine commandLine, ServerDescriptor descriptor, Action<HostContext> configure, LogLevel logLevel, CancellationToken cancellationToken)
    {
        // The transport writes to the raw stdout stream; anything else that reaches Console.Out (a stray
        // Console.WriteLine in a tool, a user script) would corrupt the protocol, so route it to stderr.
        Console.SetOut(Console.Error);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
        builder.Configuration.AddEnvironmentVariables();
        ConfigureLogging(builder.Logging, logLevel);

        var runtime = new ServerRuntimeInfo { Descriptor = descriptor, Transport = "stdio" };
        var mcp = builder.Services.AddMcpServer(options => ApplyServerOptions(options, descriptor)).WithStdioServerTransport();
        RegisterShared(builder.Services, mcp, runtime);
        configure(new HostContext(builder.Services, mcp, builder.Configuration, commandLine, runtime));

        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunHttpAsync(string[] args, CommandLine commandLine, ServerDescriptor descriptor, Action<HostContext> configure, LogLevel logLevel, CancellationToken cancellationToken)
    {
        var http = HttpHostOptions.Parse(commandLine);
        var url = http.ListenUrl;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args });
        builder.WebHost.UseUrls(url);
        builder.Configuration["AllowedHosts"] = http.AllowedHostsSetting;
        ConfigureLogging(builder.Logging, logLevel);
        ConfigureForwardedHeaders(builder.Services);
        if (http.CorsEnabled)
        {
            ConfigureCors(builder.Services, http.CorsOrigins);
        }

        var runtime = new ServerRuntimeInfo
        {
            Descriptor = descriptor,
            Transport = "streamable-http",
            HttpEndpoint = http.AdvertisedMcpEndpoint,
        };
        runtime.Configuration["authRequired"] = http.AuthRequired;
        runtime.Configuration["bind"] = http.Bind;
        runtime.Configuration["port"] = http.Port;
        runtime.Configuration["cors"] = http.CorsEnabled;
        if (http.PublicUrl is not null)
        {
            runtime.Configuration["publicUrl"] = http.PublicUrl;
        }

        var mcp = builder.Services.AddMcpServer(options => ApplyServerOptions(options, descriptor)).WithHttpTransport();
        RegisterShared(builder.Services, mcp, runtime);
        configure(new HostContext(builder.Services, mcp, builder.Configuration, commandLine, runtime));

        var app = builder.Build();
        app.UseForwardedHeaders();
        if (http.CorsEnabled)
        {
            app.UseCors("Mcp");
        }

        if (http.AuthRequired)
        {
            app.Use(CreateBearerMiddleware(http.AuthToken!));
        }

        app.MapMcp(DefaultHttpPath);

        IResult Health() => Results.Ok(new { status = "ok", server = descriptor.Name, version = descriptor.Version });
        app.MapGet("/healthz", Health);
        app.MapGet("/health", Health);

        app.Logger.LogInformation(
            "{Server} {Version} listening on {Url}{Path} (advertised {Advertised}, authRequired {AuthRequired})",
            descriptor.Name,
            descriptor.Version,
            url,
            DefaultHttpPath,
            http.AdvertisedMcpEndpoint,
            http.AuthRequired);
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static Func<HttpContext, RequestDelegate, Task> CreateBearerMiddleware(string token) =>
        async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(DefaultHttpPath, StringComparison.OrdinalIgnoreCase))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (!BearerAuthentication.Matches(context.Request.Headers.Authorization, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = BearerAuthentication.Challenge;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        };

    private static void ConfigureForwardedHeaders(IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            // Trust whatever sits in front (Docker reverse proxy, cloudflared). The bind address and
            // bearer token are the access control; spoofed proto/host only affect advertised URLs.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    private static void ConfigureCors(IServiceCollection services, IReadOnlyList<string> origins)
    {
        services.AddCors(options => options.AddPolicy("Mcp", policy =>
        {
            if (origins.Any(o => o == "*"))
            {
                policy.AllowAnyOrigin();
            }
            else
            {
                policy.WithOrigins([.. origins]);
            }

            policy
                .WithMethods("POST", "GET", "DELETE", "OPTIONS")
                .WithHeaders(
                    "Content-Type",
                    "Authorization",
                    "MCP-Protocol-Version",
                    "Mcp-Session-Id",
                    "Last-Event-ID")
                .WithExposedHeaders("Mcp-Session-Id", "WWW-Authenticate");
        }));
    }

    private static void RegisterShared(IServiceCollection services, IMcpServerBuilder mcp, ServerRuntimeInfo runtime)
    {
        services.AddSingleton(runtime);
        mcp.WithTools<ServerInfoTool>(ToolJson.Options);
    }

    private static void ApplyServerOptions(McpServerOptions options, ServerDescriptor descriptor)
    {
        options.ServerInfo = new Implementation { Name = descriptor.Name, Version = descriptor.Version, Description = descriptor.Description };
        options.ServerInstructions = descriptor.Instructions;
    }

    private static void ConfigureLogging(ILoggingBuilder logging, LogLevel level)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(level);
        logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    }

    private static LogLevel ParseLogLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : LogLevel.Information;

    private static void PrintHelp(ServerDescriptor descriptor)
    {
        Console.Out.WriteLine($"{descriptor.Name} {descriptor.Version}");
        Console.Out.WriteLine(descriptor.Description);
        Console.Out.WriteLine();
        if (!string.IsNullOrWhiteSpace(descriptor.Usage))
        {
            Console.Out.WriteLine(descriptor.Usage.TrimEnd());
            Console.Out.WriteLine();
        }

        Console.Out.WriteLine(SharedUsage.TrimEnd());
    }
}

/// <summary>Thrown during <see cref="McpServerHost.RunAsync"/> configuration for user-facing startup errors (bad arguments, missing config).</summary>
public sealed class ServerStartupException(string message, Exception? innerException = null) : Exception(message, innerException);
