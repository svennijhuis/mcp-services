using McpServices.Hosting;
using McpServices.Omni;
using Microsoft.Extensions.DependencyInjection;

var descriptor = new ServerDescriptor("mcp-omni", "One stdio process that discovers and invokes the other mcp-services HTTP servers.")
{
    Instructions = "Call discover_servers, then discover_tools, then get_tool_schema, then invoke_tool. Pass a server id from the registry. A URL is not accepted. Disabled, unknown, or unreachable servers return a tool error. discover_tools without a server id keeps going and returns an error row for a down backend.",
    Usage = """
        Usage: mcp-omni [transport options] [--registry <path>] [--timeout <seconds>]

        The registry defaults to registry.json next to this executable.
        Override it with --registry or MCP_OMNI_REGISTRY.
        The checked-in registry uses Docker compose DNS and leaves every server disabled.
        Docker compose mounts an enabled copy of that catalog.
        For a laptop, point the registry at http://127.0.0.1:5100/mcp through 5104.
        """,
};

return await McpServerHost.RunAsync(args, descriptor, context =>
{
    var options = OmniOptions.From(context.Args);
    var registry = OmniRegistry.Load(options.RegistryPath);
    context.Expose("registry", options.RegistryPath);
    context.Expose("protocol", OmniOptions.ProtocolVersion);
    context.Expose("servers", registry.Servers.Select(server => new
    {
        id = server.Id,
        title = server.Title,
        enabled = server.Enabled,
        role = server.Role,
        projection = server.Projection,
        maxChars = server.MaxChars,
    }).ToList());
    context.Services.AddSingleton(options);
    context.Services.AddSingleton(registry);
    context.Services.AddSingleton<OmniGateway>();
    context.Mcp.WithTools<OmniTools>(ToolJson.Options);
});
