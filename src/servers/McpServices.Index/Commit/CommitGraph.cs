namespace McpServices.Index.Commit;

internal static class EdgeKinds
{
    public const string Calls = "calls";
    public const string Implements = "implements";
    public const string References = "references";
}

internal static class OccurrenceRoles
{
    public const string Definition = "definition";
    public const string Reference = "reference";
}

internal static class RationaleStatus
{
    public const string Active = "active";
    public const string Stale = "stale";
    public const string Superseded = "superseded";
    public const string Rejected = "rejected";
}

/// <summary>In-memory rows for one commit rebuild. The migration in <c>IndexSchema</c> is the stored shape.</summary>
internal sealed class CommitGraph
{
    public List<FileFact> Files { get; } = [];

    public List<SymbolFact> Symbols { get; } = [];

    public List<OccurrenceFact> Occurrences { get; } = [];

    public List<EdgeFact> Edges { get; } = [];

    public List<PendingEdge> Pending { get; } = [];

    public void AddSymbol(SymbolFact symbol)
    {
        Symbols.Add(symbol);
        Occurrences.Add(new OccurrenceFact(symbol.SymbolKey, symbol.Path, symbol.StartLine, symbol.StartCol, symbol.EndLine, symbol.EndCol, OccurrenceRoles.Definition));
    }
}

internal sealed record FileFact(string Path, string ContentHash, string Language);

internal sealed record SymbolFact(
    string SymbolKey,
    string? ScipSymbol,
    string Path,
    string Kind,
    string Name,
    int StartLine,
    int StartCol,
    int EndLine,
    int EndCol,
    string ContentHash,
    string? Doc,
    string Signature);

internal sealed record OccurrenceFact(string SymbolKey, string Path, int StartLine, int StartCol, int EndLine, int EndCol, string Role);

internal sealed record EdgeFact(string FromKey, string ToKey, string Kind);

internal sealed record PendingEdge(string FromKey, string TargetName, string Kind, string Path, int Line, int Col);

internal static class SymbolKeys
{
    public static string Fallback(string path, string kind, string name, int startLine, int startCol) =>
        string.Join('\u001f', path, kind, name, startLine.ToString(System.Globalization.CultureInfo.InvariantCulture), startCol.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
