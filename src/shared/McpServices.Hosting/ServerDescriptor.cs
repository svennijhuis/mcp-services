using System.Reflection;

namespace McpServices.Hosting;

/// <summary>Static identity of a server: what it is called and what it does.</summary>
public sealed record ServerDescriptor(string Name, string Description)
{
    public string Version { get; init; } = ResolveVersion();

    /// <summary>Optional instructions the server sends to clients during initialization.</summary>
    public string? Instructions { get; init; }

    /// <summary>Short usage text printed for <c>--help</c>, after the shared transport options.</summary>
    public string? Usage { get; init; }

    /// <summary>
    /// Server-specific boolean flags (without <c>--</c>). Declaring them prevents the parser from
    /// swallowing the following argument as the flag's value.
    /// </summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ServerDescriptor).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

/// <summary>Runtime state of a server, exposed through the shared <c>server_info</c> tool.</summary>
public sealed class ServerRuntimeInfo
{
    public required ServerDescriptor Descriptor { get; init; }

    public required string Transport { get; init; }

    public string? HttpEndpoint { get; init; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Non-secret configuration values a server wants to make visible to agents.</summary>
    public IDictionary<string, object?> Configuration { get; } = new SortedDictionary<string, object?>(StringComparer.Ordinal);
}
