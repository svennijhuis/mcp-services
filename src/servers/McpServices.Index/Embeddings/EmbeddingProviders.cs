using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpServices.Hosting;
using Microsoft.Extensions.AI;

namespace McpServices.Index.Embeddings;

/// <summary>
/// Parses <c>--embeddings</c> specs into an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>:
/// <c>ollama:nomic-embed-text[@http://localhost:11434]</c> or
/// <c>openai:text-embedding-3-small[@https://api.openai.com]</c> (any OpenAI-compatible endpoint:
/// LM Studio, vLLM, Azure AI Foundry, OpenRouter). Keys come from the environment only.
/// </summary>
public static class EmbeddingProviders
{
    public static (IEmbeddingGenerator<string, Embedding<float>> Generator, string Fingerprint) Create(string spec, HttpClient? httpClient = null)
    {
        ToolGuard.NotEmpty(spec, "embeddings");
        var colon = spec.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            throw new ServerStartupException($"Invalid --embeddings '{spec}'. Use ollama:<model>[@url] or openai:<model>[@url].");
        }

        var provider = spec[..colon].ToLowerInvariant();
        var rest = spec[(colon + 1)..];
        var at = rest.IndexOf('@', StringComparison.Ordinal);
        var model = at > 0 ? rest[..at] : rest;
        var url = at > 0 ? rest[(at + 1)..] : Environment.GetEnvironmentVariable("MCP_INDEX_EMBEDDINGS_URL");
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        return provider switch
        {
            "ollama" => (new OllamaEmbeddingGenerator(client, new Uri(url ?? "http://localhost:11434"), model), $"ollama:{model}"),
            "openai" or "openai-compatible" => (new OpenAiEmbeddingGenerator(client, new Uri(url ?? "https://api.openai.com"), model, Environment.GetEnvironmentVariable("OPENAI_API_KEY")), $"openai:{model}"),
            _ => throw new ServerStartupException($"Unknown embedding provider '{provider}'. Use ollama or openai."),
        };
    }
}

/// <summary>Minimal client for Ollama's <c>/api/embed</c>.</summary>
public sealed class OllamaEmbeddingGenerator(HttpClient http, Uri baseUri, string model) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = values.ToList();
        using var response = await http.PostAsJsonAsync(new Uri(baseUri, "/api/embed"), new { model, input, truncate = true }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EmbeddingException($"Ollama returned {(int)response.StatusCode} for model '{model}' at {baseUri}: {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new EmbeddingException("Ollama returned an empty response.");
        var result = new GeneratedEmbeddings<Embedding<float>>(payload.Embeddings.Select(e => new Embedding<float>(e) { ModelId = model }));
        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private sealed record OllamaResponse([property: JsonPropertyName("embeddings")] float[][] Embeddings);
}

/// <summary>Client for the OpenAI <c>/v1/embeddings</c> shape, shared by many local and hosted servers.</summary>
public sealed class OpenAiEmbeddingGenerator(HttpClient http, Uri baseUri, string model, string? apiKey) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var path = baseUri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? "embeddings" : "v1/embeddings";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + path))
        {
            Content = JsonContent.Create(new { model, input = values.ToList(), encoding_format = "float" }),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EmbeddingException($"Embedding endpoint returned {(int)response.StatusCode} for model '{model}' at {baseUri}: {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OpenAiResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new EmbeddingException("Embedding endpoint returned an empty response.");
        return new GeneratedEmbeddings<Embedding<float>>(payload.Data.OrderBy(d => d.Index).Select(d => new Embedding<float>(d.Embedding) { ModelId = model }));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private sealed record OpenAiResponse([property: JsonPropertyName("data")] List<OpenAiItem> Data);

    private sealed record OpenAiItem([property: JsonPropertyName("index")] int Index, [property: JsonPropertyName("embedding")] float[] Embedding);
}

public sealed class EmbeddingException(string message, Exception? inner = null) : Exception(message, inner);
