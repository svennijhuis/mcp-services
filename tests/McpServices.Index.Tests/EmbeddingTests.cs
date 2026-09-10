using System.Net;
using System.Text;
using System.Text.Json;
using McpServices.Index.Embeddings;
using McpServices.TestSupport;

namespace McpServices.Index.Tests;

public class EmbeddingProviderTests
{
    [Theory]
    [InlineData("ollama:nomic-embed-text", "ollama:nomic-embed-text", typeof(OllamaEmbeddingGenerator))]
    [InlineData("ollama:nomic-embed-text@http://box:11434", "ollama:nomic-embed-text", typeof(OllamaEmbeddingGenerator))]
    [InlineData("openai:text-embedding-3-small", "openai:text-embedding-3-small", typeof(OpenAiEmbeddingGenerator))]
    [InlineData("OpenAI:local@http://localhost:1234/v1", "openai:local", typeof(OpenAiEmbeddingGenerator))]
    public void Parses_specs(string spec, string fingerprint, Type generatorType)
    {
        using var http = new HttpClient();
        var (generator, actual) = EmbeddingProviders.Create(spec, http);
        using (generator)
        {
            Assert.Equal(fingerprint, actual);
            Assert.IsType(generatorType, generator);
        }
    }

    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("cohere:embed")]
    [InlineData("")]
    public void Rejects_invalid_specs(string spec)
    {
        using var http = new HttpClient();
        Assert.ThrowsAny<Exception>(() => EmbeddingProviders.Create(spec, http));
    }
}

/// <summary>
/// A fake OpenAI-compatible embeddings endpoint: deterministic hashed bag-of-words vectors, so
/// texts sharing words are close. Enough to prove the semantic path end to end without a model.
/// </summary>
public sealed class FakeEmbeddingServer : IDisposable
{
    private const int Dimensions = 64;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public FakeEmbeddingServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public int Requests { get; private set; }

    public static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var token in text.ToLowerInvariant().Split([' ', '\n', '\r', '\t', '.', ',', ';', '(', ')', '<', '>', '/', '"'], StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = 17;
            foreach (var c in token)
            {
                hash = unchecked(hash * 31 + c);
            }

            vector[Math.Abs(hash % Dimensions)] += 1;
        }

        return vector;
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }

            Requests++;
            using var reader = new StreamReader(context.Request.InputStream);
            var body = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
            var data = body.GetProperty("input").EnumerateArray().Select((item, index) => new { index, embedding = Embed(item.GetString()!), @object = "embedding" }).ToList();
            var response = JsonSerializer.SerializeToUtf8Bytes(new { data, model = body.GetProperty("model").GetString() });
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(response);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}

public sealed class EmbeddingServerFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcp-index-embed-" + Guid.NewGuid().ToString("N"));

    public string StorePath { get; } = Path.Combine(Path.GetTempPath(), "mcp-index-embed-" + Guid.NewGuid().ToString("N") + ".db");

    public FakeEmbeddingServer Embeddings { get; } = new();

    public ServerFixture Server { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(Root, "src", "Payments.cs"), """
            namespace Shop;

            /// <summary>Charges the customer card and records the receipt.</summary>
            public sealed class PaymentGateway
            {
                public Task ChargeAsync(decimal amount) => Task.CompletedTask;
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(Root, "src", "Shipping.cs"), """
            namespace Shop;

            /// <summary>Books a parcel with the carrier and prints the label.</summary>
            public sealed class ShippingBooker
            {
                public Task BookAsync(string address) => Task.CompletedTask;
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(Root, "README.md"), "# Shop\n\nCustomer card charges go through the payment gateway; parcel labels come from the shipping booker.\n");

        Server = await ServerFixture.StartAsync(
            "McpServices.Index",
            ["--root", Root, "--store", $"sqlite:{StorePath}", "--embeddings", $"openai:fake@{Embeddings.BaseUrl}", "--watch", "--log-level", "Warning"],
            new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "test-key" });
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        Embeddings.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(StorePath)!, Path.GetFileName(StorePath) + "*"))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
    }
}

public class EmbeddingServerTests(EmbeddingServerFixture fixture) : IClassFixture<EmbeddingServerFixture>
{
    [Fact]
    public async Task Indexing_embeds_chunks_and_status_reports_them()
    {
        var result = await fixture.Server.CallJsonAsync("index_repository", new { root = fixture.Root });
        var embeddings = result.GetProperty("status").GetProperty("embeddings");

        Assert.Equal("openai:fake", embeddings.GetProperty("model").GetString());
        Assert.True(embeddings.GetProperty("total").GetInt32() > 0);
        Assert.Equal(embeddings.GetProperty("total").GetInt32(), embeddings.GetProperty("embedded").GetInt32());
        Assert.Equal(0, embeddings.GetProperty("pending").GetInt32());
        Assert.True(fixture.Embeddings.Requests > 0);
    }

    [Fact]
    public async Task Semantic_hits_join_the_fused_ranking()
    {
        await fixture.Server.CallJsonAsync("index_repository", new { root = fixture.Root });

        // No keyword overlap with symbol names: only the doc comment/README share words with the query.
        var result = await fixture.Server.CallJsonAsync("search_code", new { query = "charges the customer card", root = fixture.Root });

        Assert.True(result.GetProperty("semantic").GetBoolean());
        var paths = result.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("path").GetString()).ToList();
        Assert.Contains("src/Payments.cs", paths);
        var shipping = paths.IndexOf("src/Shipping.cs");
        Assert.True(shipping == -1 || paths.IndexOf("src/Payments.cs") < shipping, "Payments should outrank Shipping: " + string.Join(", ", paths));
    }

    [Fact]
    public async Task Server_info_exposes_embedding_model()
    {
        var info = await fixture.Server.CallJsonAsync("server_info");
        var text = info.GetRawText();
        Assert.Contains("openai:fake", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fake_embeddings_are_similar_for_shared_words()
    {
        var a = FakeEmbeddingServer.Embed("charges the customer card");
        var b = FakeEmbeddingServer.Embed("Charges the customer card and records the receipt.");
        var c = FakeEmbeddingServer.Embed("Books a parcel with the carrier and prints the label.");
        Assert.True(Cosine(a, b) > Cosine(a, c));
    }

    private static float Cosine(float[] x, float[] y)
    {
        float dot = 0, nx = 0, ny = 0;
        for (var i = 0; i < x.Length; i++)
        {
            dot += x[i] * y[i];
            nx += x[i] * x[i];
            ny += y[i] * y[i];
        }

        return dot / MathF.Sqrt(nx * ny);
    }
}
