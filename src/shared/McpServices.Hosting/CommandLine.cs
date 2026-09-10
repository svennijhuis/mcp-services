namespace McpServices.Hosting;

/// <summary>
/// Minimal argument parser shared by all servers. Supports <c>--flag</c>, <c>--name value</c>,
/// <c>--name=value</c>, repeated options and positional arguments. Everything after <c>--</c> is positional.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positionals = [];

    public IReadOnlyList<string> Positionals => _positionals;

    public IReadOnlyList<string> RawArgs { get; }

    private CommandLine(string[] args)
    {
        RawArgs = args;
    }

    public static CommandLine Parse(string[] args, params string[] flagNames)
    {
        ArgumentNullException.ThrowIfNull(args);
        var flags = new HashSet<string>(flagNames, StringComparer.OrdinalIgnoreCase);
        var result = new CommandLine(args);
        var positionalOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (positionalOnly || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                result._positionals.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                positionalOnly = true;
                continue;
            }

            var body = arg[2..];
            var eq = body.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                result.Add(body[..eq], body[(eq + 1)..]);
                continue;
            }

            var isFlag = flags.Contains(body);
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            if (!isFlag && hasValue)
            {
                result.Add(body, args[++i]);
            }
            else
            {
                result._flags.Add(body);
            }
        }

        return result;
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var list))
        {
            list = [];
            _options[name] = list;
        }

        list.Add(value);
    }

    public bool HasFlag(string name) => _flags.Contains(name) || _options.ContainsKey(name);

    public string? GetOption(string name) =>
        _options.TryGetValue(name, out var list) ? list[^1] : null;

    public string? GetOption(string name, string? environmentVariable) =>
        GetOption(name) ?? (environmentVariable is null ? null : Environment.GetEnvironmentVariable(environmentVariable));

    public IReadOnlyList<string> GetOptions(string name) =>
        _options.TryGetValue(name, out var list) ? list : [];

    public int GetInt(string name, int defaultValue) =>
        int.TryParse(GetOption(name), out var value) ? value : defaultValue;

    public bool GetBool(string name, bool defaultValue)
    {
        if (_flags.Contains(name))
        {
            return true;
        }

        var raw = GetOption(name);
        return raw is null ? defaultValue : raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
