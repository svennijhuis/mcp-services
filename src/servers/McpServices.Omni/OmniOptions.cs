using System.Globalization;
using McpServices.Hosting;

namespace McpServices.Omni;

public sealed record OmniOptions(string RegistryPath, TimeSpan Timeout)
{
    public const string ProtocolVersion = "2025-06-18";

    public static OmniOptions From(CommandLine args)
    {
        var path = args.GetOption("registry", "MCP_OMNI_REGISTRY");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "registry.json");
        }

        if (!File.Exists(path))
        {
            throw new ServerStartupException($"Registry file not found: {path}. Set MCP_OMNI_REGISTRY or pass --registry.");
        }

        var rawTimeout = args.GetOption("timeout", "MCP_OMNI_TIMEOUT_SECONDS");
        var seconds = 15;
        if (!string.IsNullOrWhiteSpace(rawTimeout))
        {
            if (!int.TryParse(rawTimeout, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > 120)
            {
                throw new ServerStartupException("--timeout must be a whole number of seconds from 1 to 120.");
            }
        }

        return new OmniOptions(Path.GetFullPath(path), TimeSpan.FromSeconds(seconds));
    }
}
