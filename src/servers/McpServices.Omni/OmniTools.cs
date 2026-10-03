using System.ComponentModel;
using System.Text.Json;
using McpServices.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServices.Omni;

[McpServerToolType]
public sealed class OmniTools(OmniRegistry registry, OmniGateway gateway)
{
    [McpServerTool(Name = "discover_servers", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Discover servers")]
    [Description("List enabled backend servers. Returns id, title, and summary only. Disabled servers are omitted. Pass a server id to the other tools. A URL is not accepted.")]
    public object DiscoverServers(RequestContext<CallToolRequestParams> context)
    {
        RejectUrl(context);
        return new
        {
            servers = registry.Enabled.Select(server => new { id = server.Id, title = server.Title, summary = server.Summary }).ToList(),
        };
    }

    [McpServerTool(Name = "discover_tools", ReadOnly = true, Idempotent = true, OpenWorld = true, Title = "Discover tools")]
    [Description("List tool names and a one-line description for one enabled server, or for every enabled server when server is omitted. Does not return schemas. A URL is not accepted.")]
    public async Task<object> DiscoverTools(
        RequestContext<CallToolRequestParams> context,
        [Description("Server id from discover_servers. Omit to list every enabled server.")] string? server = null,
        CancellationToken cancellationToken = default)
    {
        RejectUrl(context);
        var targets = string.IsNullOrWhiteSpace(server) ? registry.Enabled : [registry.Require(server)];
        var rows = new List<object>();
        foreach (var entry in targets)
        {
            var tools = await gateway.ListToolsAsync(entry, cancellationToken).ConfigureAwait(false);
            foreach (var tool in tools.OrderBy(tool => tool.Name, StringComparer.Ordinal))
            {
                rows.Add(new { server = entry.Id, name = tool.Name, description = OmniGateway.OneLine(tool.Description) });
            }
        }

        return new { tools = rows };
    }

    [McpServerTool(Name = "get_tool_schema", ReadOnly = true, Idempotent = true, OpenWorld = true, Title = "Get tool schema")]
    [Description("Return one tool: name, description, and inputSchema. Call discover_tools first. A URL is not accepted.")]
    public async Task<object> GetToolSchema(
        [Description("Server id from discover_servers.")] string server,
        [Description("Tool name from discover_tools.")] string tool,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default)
    {
        RejectUrl(context);
        var entry = registry.Require(server);
        var name = ToolGuard.NotEmpty(tool, "tool");
        var tools = await gateway.ListToolsAsync(entry, cancellationToken).ConfigureAwait(false);
        var match = tools.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (match is null)
        {
            throw new ToolException($"Tool '{name}' was not found on server '{entry.Id}'. Call discover_tools.");
        }

        return new { name = match.Name, description = match.Description ?? string.Empty, inputSchema = match.InputSchema };
    }

    [McpServerTool(Name = "invoke_tool", ReadOnly = false, Idempotent = false, OpenWorld = true, Title = "Invoke tool")]
    [Description("Call one backend tool by server id and tool name, then truncate the text result to that server's maxChars. A URL is not accepted.")]
    public async Task<string> InvokeTool(
        [Description("Server id from discover_servers.")] string server,
        [Description("Tool name from discover_tools.")] string tool,
        RequestContext<CallToolRequestParams> context,
        [Description("Arguments object forwarded to the backend tool.")] JsonElement? arguments = null,
        CancellationToken cancellationToken = default)
    {
        RejectUrl(context);
        var entry = registry.Require(server);
        var name = ToolGuard.NotEmpty(tool, "tool");
        return await gateway.InvokeAsync(entry, name, BindArguments(arguments), cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, object?>? BindArguments(JsonElement? arguments)
    {
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (arguments.Value.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("Parameter 'arguments' must be a JSON object.");
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.Value.EnumerateObject())
        {
            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static readonly string[] UrlKeys =
    [
        "url", "uri", "baseUrl", "base_url", "endpoint", "healthUrl", "health_url", "mcpUrl", "mcp_url", "serverUrl", "server_url",
    ];

    private static void RejectUrl(RequestContext<CallToolRequestParams> context)
    {
        var arguments = context.Params?.Arguments;
        if (arguments is null)
        {
            return;
        }

        foreach (var pair in arguments)
        {
            if (pair.Key.Equals("arguments", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("args", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (UrlKeys.Any(key => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                && pair.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                throw new ToolException("Pass a server id from the registry. A URL is not accepted.");
            }

            if (pair.Value.ValueKind == JsonValueKind.String && IsHttpUrl(pair.Value.GetString()))
            {
                throw new ToolException("Pass a server id from the registry. A URL is not accepted.");
            }
        }
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
