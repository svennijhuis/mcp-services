using McpServices.Hosting;

namespace McpServices.Roslyn;

public sealed class RoslynOptions
{
    public IReadOnlyList<string> Roots { get; init; } = [];

    public bool RestrictToRoots { get; init; }

    public bool ScriptingEnabled { get; init; } = true;

    public bool BuildEnabled { get; init; } = true;

    public int MaxResults { get; init; } = 2000;

    public static RoslynOptions From(CommandLine args)
    {
        var roots = args.GetOptions("root").Concat(args.Positionals).ToList();
        if (roots.Count == 0 && Environment.GetEnvironmentVariable("MCP_ROSLYN_ROOT") is { Length: > 0 } envRoot)
        {
            roots.Add(envRoot);
        }

        if (roots.Count == 0)
        {
            roots.Add(Directory.GetCurrentDirectory());
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                throw new ServerStartupException($"Root '{root}' does not exist.");
            }
        }

        return new RoslynOptions
        {
            Roots = roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToList(),
            RestrictToRoots = args.HasFlag("restrict"),
            ScriptingEnabled = !args.HasFlag("no-scripting"),
            BuildEnabled = !args.HasFlag("no-build"),
            MaxResults = Math.Clamp(args.GetInt("max-results", 2000), 50, 20000),
        };
    }

    public void EnsureAllowed(string path)
    {
        if (!RestrictToRoots)
        {
            return;
        }

        var full = Path.GetFullPath(path);
        if (!Roots.Any(r => full.Equals(r, StringComparison.Ordinal) || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new ToolException($"'{path}' is outside the configured roots ({string.Join(", ", Roots)}).");
        }
    }
}
