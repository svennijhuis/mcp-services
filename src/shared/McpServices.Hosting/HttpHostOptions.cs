using System.Net;

namespace McpServices.Hosting;

/// <summary>
/// Parsed Streamable HTTP bind, auth and advertisement settings shared by every server.
/// </summary>
public sealed class HttpHostOptions
{
    public const string DefaultBind = "127.0.0.1";
    public const int DefaultPort = 5100;

    public required int Port { get; init; }

    public required string Bind { get; init; }

    /// <summary>Shared secret for <c>Authorization: Bearer</c>. Null means the endpoint is unauthenticated.</summary>
    public string? AuthToken { get; init; }

    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    public IReadOnlyList<string> CorsOrigins { get; init; } = [];

    /// <summary>Public HTTPS origin (tunnel or reverse proxy) advertised in <c>server_info</c>.</summary>
    public string? PublicUrl { get; init; }

    public bool AuthRequired => !string.IsNullOrEmpty(AuthToken);

    public bool CorsEnabled => CorsOrigins.Count > 0;

    public bool AllowAnyHost => AllowedHosts.Count == 0 || AllowedHosts.Any(h => h == "*");

    public string ListenUrl => $"http://{FormatBind(Bind)}:{Port}";

    /// <summary>URL clients should call. Prefers <see cref="PublicUrl"/> so Docker's 0.0.0.0 bind is not advertised.</summary>
    public string AdvertisedMcpEndpoint => ToMcpEndpoint(PublicUrl ?? ListenUrl);

    public string AllowedHostsSetting => AllowAnyHost ? "*" : string.Join(';', DistinctHosts());

    public static HttpHostOptions Parse(CommandLine args, Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        var bind = FirstNonEmpty(args.GetOption("host"), env("MCP_HOST")) ?? DefaultBind;
        var port = ParsePort(args.GetOption("port") ?? env("MCP_PORT"));
        var token = FirstNonEmpty(args.GetOption("auth-token"), env("MCP_AUTH_TOKEN"));
        if (args.HasFlag("auth-token") && args.GetOption("auth-token") is null)
        {
            throw new ServerStartupException("--auth-token requires a value (or set MCP_AUTH_TOKEN).");
        }

        var publicUrl = NormalizePublicUrl(FirstNonEmpty(args.GetOption("public-url"), env("MCP_PUBLIC_URL")));
        var cors = MergeList(args.GetOptions("cors-origin"), env("MCP_CORS_ORIGINS"));
        var allowed = MergeList(args.GetOptions("allowed-host"), env("MCP_ALLOWED_HOSTS"));

        if (!IsLoopbackBind(bind) && string.IsNullOrEmpty(token))
        {
            throw new ServerStartupException(
                $"HTTP bind address '{bind}' is not loopback; set --auth-token or MCP_AUTH_TOKEN before exposing the server.");
        }

        return new HttpHostOptions
        {
            Port = port,
            Bind = bind,
            AuthToken = token,
            PublicUrl = publicUrl,
            CorsOrigins = cors,
            AllowedHosts = BuildAllowedHosts(bind, publicUrl, allowed),
        };
    }

    public IEnumerable<string> DistinctHosts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in AllowedHosts)
        {
            if (seen.Add(host))
            {
                yield return host;
            }
        }
    }

    public static bool IsLoopbackBind(string bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        var trimmed = StripBrackets(bind.Trim());
        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed is "127.0.0.1" or "::1")
        {
            return true;
        }

        return IPAddress.TryParse(trimmed, out var address) && IPAddress.IsLoopback(address);
    }

    public static string FormatBind(string bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        var trimmed = bind.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            return trimmed;
        }

        return trimmed.Contains(':', StringComparison.Ordinal) ? $"[{trimmed}]" : trimmed;
    }

    public static string ToMcpEndpoint(string basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        var trimmed = basis.Trim().TrimEnd('/');
        return trimmed.EndsWith(McpServerHost.DefaultHttpPath, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + McpServerHost.DefaultHttpPath;
    }

    internal static IReadOnlyList<string> MergeList(IEnumerable<string> fromArgs, string? fromEnvironment)
    {
        var result = new List<string>();
        foreach (var value in fromArgs)
        {
            AppendParts(result, value);
        }

        AppendParts(result, fromEnvironment);
        return result;
    }

    private static List<string> BuildAllowedHosts(string bind, string? publicUrl, IReadOnlyList<string> extra)
    {
        if (extra.Any(h => h == "*"))
        {
            return ["*"];
        }

        var hosts = new List<string> { "localhost", "127.0.0.1", "[::1]", "::1" };
        var bindHost = StripBrackets(bind);
        if (!IsWildcardBind(bindHost) && !hosts.Contains(bindHost, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(bind);
        }

        foreach (var host in extra)
        {
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                hosts.Add(host);
            }
        }

        if (publicUrl is not null && Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host)
            && !hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(uri.Host);
        }

        return hosts;
    }

    private static void AppendParts(List<string> target, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        foreach (var part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!target.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(part);
            }
        }
    }

    private static int ParsePort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultPort;
        }

        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
        {
            throw new ServerStartupException($"Invalid HTTP port '{raw}'.");
        }

        return port;
    }

    private static string? NormalizePublicUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ServerStartupException("--public-url / MCP_PUBLIC_URL must be an absolute http(s) URL.");
        }

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool IsWildcardBind(string bind) =>
        bind is "0.0.0.0" or "::" or "[::]" or "*";

    private static string StripBrackets(string bind) =>
        bind.StartsWith('[') && bind.EndsWith(']') && bind.Length > 2 ? bind[1..^1] : bind;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
