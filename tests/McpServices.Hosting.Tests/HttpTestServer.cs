using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;

namespace McpServices.Hosting.Tests;

/// <summary>Boots <see cref="McpServerHost"/> over loopback Streamable HTTP for integration tests.</summary>
internal sealed class HttpTestServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task<int> _run;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private HttpTestServer(Uri baseAddress, Task<int> run, CancellationTokenSource cts)
    {
        BaseAddress = baseAddress;
        _run = run;
        _cts = cts;
    }

    public Uri BaseAddress { get; }

    public Uri McpUri => new(BaseAddress, "mcp");

    public HttpClient Http => _http;

    public static async Task<HttpTestServer> StartAsync(params string[] extraArgs)
    {
        var port = GetFreePort();
        var args = new List<string> { "--http", "--host", "127.0.0.1", "--port", port.ToString(), "--log-level", "Warning" };
        args.AddRange(extraArgs);

        var descriptor = new ServerDescriptor("mcp-host-test", "HTTP host tests");
        var cts = new CancellationTokenSource();
        var run = McpServerHost.RunAsync(args.ToArray(), descriptor, _ => { }, cts.Token);
        var server = new HttpTestServer(new Uri($"http://127.0.0.1:{port}/"), run, cts);
        try
        {
            await server.WaitUntilHealthyAsync().ConfigureAwait(false);
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task WaitUntilHealthyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_run.IsCompleted)
            {
                var code = await _run.ConfigureAwait(false);
                throw new InvalidOperationException($"HTTP test server exited with code {code} before becoming healthy.");
            }

            try
            {
                using var response = await _http.GetAsync(new Uri(BaseAddress, "healthz")).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                last = new InvalidOperationException($"healthz returned {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException)
            {
                last = ex;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"HTTP test server did not become healthy at {BaseAddress}. Last error: {last}");
    }

    public async Task<McpClient> ConnectAsync(string? bearerToken = null)
    {
        IDictionary<string, string>? headers = bearerToken is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer " + bearerToken };

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = McpUri,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }

        _http.Dispose();
        _cts.Dispose();
    }
}
