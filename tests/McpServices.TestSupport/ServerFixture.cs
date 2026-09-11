using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServices.TestSupport;

/// <summary>
/// Launches a server assembly (copied next to the test assembly through a ProjectReference) as a
/// child process over stdio and connects an SDK client to it, exactly like Cursor or Claude would.
/// </summary>
public sealed class ServerFixture : IAsyncDisposable
{
    private ServerFixture(McpClient client)
    {
        Client = client;
    }

    public McpClient Client { get; }

    /// <summary>Upper bound for server start-up (includes JIT and, for mcp-roslyn, MSBuild discovery).</summary>
    public static TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Upper bound for a single tool call; a hang surfaces as a failure naming the tool instead of stalling the run.</summary>
    public static TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public static async Task<ServerFixture> StartAsync(string serverAssemblyName, IEnumerable<string> args, IDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, serverAssemblyName + ".dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException($"Server assembly not found next to the tests: {dll}. Add a ProjectReference to the server project.", dll);
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = serverAssemblyName,
            Command = "dotnet",
            Arguments = [dll, .. args],
            EnvironmentVariables = environment,
            WorkingDirectory = AppContext.BaseDirectory,
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartupTimeout);
        try
        {
            var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token).ConfigureAwait(false);
            return new ServerFixture(client);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Server '{serverAssemblyName}' did not complete the MCP handshake within {StartupTimeout}.");
        }
    }

    public async Task<IReadOnlyList<string>> ToolNamesAsync()
    {
        var tools = await Client.ListToolsAsync().ConfigureAwait(false);
        return tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>Calls a tool and returns the text of the first content block, throwing on <c>isError</c>.</summary>
    public async Task<string> CallTextAsync(string tool, object? arguments = null)
    {
        var result = await CallAsync(tool, arguments).ConfigureAwait(false);
        var text = FirstText(result);
        if (result.IsError == true)
        {
            throw new InvalidOperationException($"Tool '{tool}' returned an error: {text}");
        }

        return text;
    }

    /// <summary>Calls a tool and parses the JSON text result.</summary>
    public async Task<JsonElement> CallJsonAsync(string tool, object? arguments = null)
    {
        var text = await CallTextAsync(tool, arguments).ConfigureAwait(false);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Calls a tool expecting an error and returns the error message.</summary>
    public async Task<string> CallExpectingErrorAsync(string tool, object? arguments = null)
    {
        var result = await CallAsync(tool, arguments).ConfigureAwait(false);
        if (result.IsError != true)
        {
            throw new InvalidOperationException($"Tool '{tool}' unexpectedly succeeded: {FirstText(result)}");
        }

        return FirstText(result);
    }

    public async Task<CallToolResult> CallAsync(string tool, object? arguments = null)
    {
        using var timeout = new CancellationTokenSource(CallTimeout);
        try
        {
            return await Client.CallToolAsync(tool, ToDictionary(arguments), cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Tool '{tool}' did not answer within {CallTimeout}.");
        }
    }

    private static IReadOnlyDictionary<string, object?>? ToDictionary(object? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        if (arguments is IReadOnlyDictionary<string, object?> ready)
        {
            return ready;
        }

        var json = JsonSerializer.SerializeToElement(arguments);
        return json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
    }

    private static string FirstText(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;

    public async ValueTask DisposeAsync() => await Client.DisposeAsync().ConfigureAwait(false);
}
