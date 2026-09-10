namespace McpServices.Index;

public enum AutoRefreshMode
{
    Inline,
    Background,
    Off,
}

/// <summary>Runtime knobs, all overridable from the command line.</summary>
public sealed class IndexOptions
{
    /// <summary>Repository roots registered at startup (<c>--root</c> or positionals). Tools may also pass an explicit root.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    /// <summary>When set, tools may only touch repositories under these roots.</summary>
    public bool RestrictToRoots { get; init; }

    public AutoRefreshMode AutoRefresh { get; init; } = AutoRefreshMode.Inline;

    /// <summary>Largest delta that is indexed inline before answering a query.</summary>
    public int InlineMaxFiles { get; init; } = 200;

    public int MaxFileKb { get; init; } = 512;

    /// <summary>Without git, freshness scans at most this many files before reporting <c>unknown</c>.</summary>
    public int FreshnessMaxFiles { get; init; } = 5000;

    /// <summary>Optional embedding model spec (<c>ollama:nomic-embed-text</c>, <c>openai:text-embedding-3-small</c>).</summary>
    public string? Embeddings { get; init; }

    public bool Watch { get; init; }
}
