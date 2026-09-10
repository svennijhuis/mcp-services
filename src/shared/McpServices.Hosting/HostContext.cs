using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServices.Hosting;

/// <summary>Everything a server needs during startup to register its services and MCP primitives.</summary>
public sealed class HostContext
{
    internal HostContext(IServiceCollection services, IMcpServerBuilder mcp, IConfiguration configuration, CommandLine args, ServerRuntimeInfo runtime)
    {
        Services = services;
        Mcp = mcp;
        Configuration = configuration;
        Args = args;
        Runtime = runtime;
    }

    public IServiceCollection Services { get; }

    public IMcpServerBuilder Mcp { get; }

    public IConfiguration Configuration { get; }

    /// <summary>Parsed command line; shared transport options (<c>--http</c>, <c>--port</c>, ...) are already consumed.</summary>
    public CommandLine Args { get; }

    public ServerRuntimeInfo Runtime { get; }

    /// <summary>Records a non-secret configuration value so <c>server_info</c> can report it.</summary>
    public HostContext Expose(string key, object? value)
    {
        Runtime.Configuration[key] = value;
        return this;
    }
}
