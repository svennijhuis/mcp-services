using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpServices.Hosting;

[McpServerToolType]
public sealed class ServerInfoTool(ServerRuntimeInfo runtime)
{
    [McpServerTool(Name = "server_info", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Server info")]
    [Description("Describes this MCP server: name, version, transport, uptime and its non-secret configuration. Call this first when you need to know what the server can reach.")]
    public object GetServerInfo() => new
    {
        name = runtime.Descriptor.Name,
        version = runtime.Descriptor.Version,
        description = runtime.Descriptor.Description,
        transport = runtime.Transport,
        httpEndpoint = runtime.HttpEndpoint,
        startedAt = runtime.StartedAt,
        uptimeSeconds = (long)(DateTimeOffset.UtcNow - runtime.StartedAt).TotalSeconds,
        machine = Environment.MachineName,
        os = Environment.OSVersion.ToString(),
        runtimeVersion = Environment.Version.ToString(),
        configuration = runtime.Configuration,
    };
}
