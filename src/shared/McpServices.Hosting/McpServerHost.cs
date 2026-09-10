using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
          --port <n>             HTTP port (default 5100)
          --host <name>          HTTP bind address (default 127.0.0.1; use 0.0.0.0 in Docker)
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
        var port = commandLine.GetInt("port", int.TryParse(Environment.GetEnvironmentVariable("MCP_PORT"), out var envPort) ? envPort : 5100);
        var bind = commandLine.GetOption("host", "MCP_HOST") ?? "127.0.0.1";
        var url = $"http://{bind}:{port}";

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args });
        builder.WebHost.UseUrls(url);
        ConfigureLogging(builder.Logging, logLevel);

        var runtime = new ServerRuntimeInfo { Descriptor = descriptor, Transport = "streamable-http", HttpEndpoint = url + DefaultHttpPath };
        var mcp = builder.Services.AddMcpServer(options => ApplyServerOptions(options, descriptor)).WithHttpTransport();
        RegisterShared(builder.Services, mcp, runtime);
        configure(new HostContext(builder.Services, mcp, builder.Configuration, commandLine, runtime));

        var app = builder.Build();
        app.MapMcp(DefaultHttpPath);
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", server = descriptor.Name, version = descriptor.Version }));

        app.Logger.LogInformation("{Server} {Version} listening on {Url}{Path}", descriptor.Name, descriptor.Version, url, DefaultHttpPath);
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
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
public sealed class ServerStartupException(string message) : Exception(message);
