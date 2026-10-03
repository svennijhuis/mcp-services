using System.Text.Json;
using McpServices.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServices.Omni;

/// <summary>
/// HTTP MCP client of one backend at a time. Initializes each server once, then calls tools/list or tools/call.
/// Does not send a bearer token or a user JWT.
/// </summary>
public sealed class OmniGateway : IAsyncDisposable, IDisposable
{
    private readonly OmniOptions _options;
    private readonly ILogger<OmniGateway> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.Ordinal);
    private bool _disposed;

    public OmniGateway(OmniOptions options, ILogger<OmniGateway> logger)
    {
        _options = options;
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<IReadOnlyList<BackendTool>> ListToolsAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        var tools = await ExecuteAsync(server, async (client, ct) =>
        {
            var listed = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            return listed.Select(tool => new BackendTool(tool.Name, tool.Description, tool.ProtocolTool.InputSchema.Clone())).ToList();
        }, cancellationToken).ConfigureAwait(false);
        return tools;
    }

    public async Task<string> InvokeAsync(ServerEntry server, string tool, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            server,
            (client, ct) => client.CallToolAsync(tool, arguments, cancellationToken: ct).AsTask(),
            cancellationToken).ConfigureAwait(false);
        var text = Project(result);
        text = Truncate(text, server.MaxChars);
        if (result.IsError == true)
        {
            throw new ToolException(string.IsNullOrEmpty(text) ? $"Tool '{tool}' on server '{server.Id}' failed." : text);
        }

        return text;
    }

    internal static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars > 0 && char.IsHighSurrogate(text[maxChars - 1]))
        {
            return text[..(maxChars - 1)];
        }

        return text[..maxChars];
    }

    internal static string OneLine(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var cut = description.IndexOfAny(['\r', '\n']);
        var line = cut < 0 ? description : description[..cut];
        return line.Trim();
    }

    private async Task<T> ExecuteAsync<T>(ServerEntry server, Func<McpClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            try
            {
                var client = await ConnectAsync(server, timeout.Token).ConfigureAwait(false);
                return await action(client, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await DropAsync(server.Id).ConfigureAwait(false);
                throw new ToolException($"Server '{server.Id}' timed out.");
            }
            catch (TimeoutException)
            {
                await DropAsync(server.Id).ConfigureAwait(false);
                throw new ToolException($"Server '{server.Id}' timed out.");
            }
            catch (ToolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await DropAsync(server.Id).ConfigureAwait(false);
                _logger.LogWarning(ex, "Backend {ServerId} failed.", server.Id);
                throw new ToolException(FailureMessage(server.Id, ex));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<McpClient> ConnectAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(server.Id, out var existing))
        {
            return existing;
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = server.BaseUrl,
                Name = server.Id,
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            _http,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);

        try
        {
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ProtocolVersion = OmniOptions.ProtocolVersion,
                    InitializationTimeout = _options.Timeout,
                    ClientInfo = new Implementation { Name = "mcp-omni", Version = "0.1.0" },
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _clients.Add(server.Id, client);
            return client;
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                _logger.LogDebug(disposeEx, "Transport dispose failed for {ServerId}.", server.Id);
            }

            throw;
        }
    }

    private async Task DropAsync(string id)
    {
        if (!_clients.Remove(id, out var client))
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Client dispose failed for {ServerId}.", id);
        }
    }

    private static string FailureMessage(string id, Exception ex)
    {
        var text = ex.ToString();
        if (text.Contains("401", StringComparison.Ordinal) || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return $"Server '{id}' rejected the call. mcp-omni does not send a bearer token.";
        }

        if (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException
            || ex.InnerException is HttpRequestException or IOException or System.Net.Sockets.SocketException
            || text.Contains("Socket", StringComparison.OrdinalIgnoreCase))
        {
            return $"Server '{id}' is unreachable.";
        }

        return $"Server '{id}' failed.";
    }

    private static string Project(CallToolResult result)
    {
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (!string.IsNullOrEmpty(text) || result.Content.Count == 0)
        {
            return text;
        }

        return JsonSerializer.Serialize(result.Content, ToolJson.Options);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();
        _http.Dispose();
        _gate.Dispose();
    }
}

public sealed record BackendTool(string Name, string? Description, JsonElement InputSchema);
