using System.Text.Json;
using McpServices.Hosting;

namespace McpServices.Omni;

/// <summary>
/// Backend catalog. <see cref="ServerEntry.Role"/> is documentation for operators and is not enforced.
/// Callers select a server by id. They cannot pass a URL.
/// </summary>
public sealed class OmniRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private OmniRegistry(string path, IReadOnlyList<ServerEntry> servers)
    {
        Path = path;
        Servers = servers;
    }

    public string Path { get; }

    public IReadOnlyList<ServerEntry> Servers { get; }

    public IReadOnlyList<ServerEntry> Enabled => Servers.Where(server => server.Enabled).ToList();

    public static OmniRegistry Load(string path)
    {
        RegistryDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RegistryDocument>(File.ReadAllText(path), Json)
                ?? throw new ServerStartupException($"Registry file is empty: {path}");
        }
        catch (JsonException ex)
        {
            throw new ServerStartupException($"Registry file is not valid JSON: {path}. {ex.Message}", ex);
        }

        if (document.Servers is null || document.Servers.Count == 0)
        {
            throw new ServerStartupException($"Registry file has no servers: {path}");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<ServerEntry>(document.Servers.Count);
        foreach (var raw in document.Servers)
        {
            var id = Required(raw.Id, "id");
            if (id.Contains(':') || id.Contains('/'))
            {
                throw new ServerStartupException($"Server id '{id}' must be a name, not a URL.");
            }

            if (!seen.Add(id))
            {
                throw new ServerStartupException($"Registry server id '{id}' is duplicated.");
            }

            var projection = Required(raw.Projection, "projection");
            if (!string.Equals(projection, "summary", StringComparison.Ordinal))
            {
                throw new ServerStartupException($"Server '{id}' projection must be 'summary'.");
            }

            var maxChars = raw.MaxChars ?? 8000;
            if (maxChars < 1 || maxChars > 200_000)
            {
                throw new ServerStartupException($"Server '{id}' maxChars must be between 1 and 200000.");
            }

            entries.Add(new ServerEntry(
                id,
                Required(raw.Title, "title"),
                Required(raw.Summary, "summary"),
                raw.Enabled,
                Required(raw.Role, "role"),
                RequiredUrl(raw.BaseUrl, id, "baseUrl"),
                RequiredUrl(raw.HealthUrl, id, "healthUrl"),
                projection,
                maxChars));
        }

        return new OmniRegistry(path, entries);
    }

    public ServerEntry Require(string? id)
    {
        var name = ToolGuard.NotEmpty(id, "server");
        var match = Servers.FirstOrDefault(server => string.Equals(server.Id, name, StringComparison.Ordinal));
        if (match is null)
        {
            throw new ToolException($"Unknown server '{name}'. Call discover_servers for the enabled ids.");
        }

        if (!match.Enabled)
        {
            throw new ToolException($"Server '{name}' is disabled.");
        }

        return match;
    }

    private static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ServerStartupException($"Registry entry is missing '{field}'.");
        }

        return value.Trim();
    }

    private static Uri RequiredUrl(string? value, string id, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ServerStartupException($"Server '{id}' {field} must be an absolute http or https URL.");
        }

        return uri;
    }

    private sealed class RegistryDocument
    {
        public List<ServerJson>? Servers { get; set; }
    }

    private sealed class ServerJson
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? Summary { get; set; }

        public bool Enabled { get; set; }

        public string? Role { get; set; }

        public string? BaseUrl { get; set; }

        public string? HealthUrl { get; set; }

        public string? Projection { get; set; }

        public int? MaxChars { get; set; }
    }
}

public sealed record ServerEntry(
    string Id,
    string Title,
    string Summary,
    bool Enabled,
    string Role,
    Uri BaseUrl,
    Uri HealthUrl,
    string Projection,
    int MaxChars);
