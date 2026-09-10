using McpServices.Storage;

namespace McpServices.Index.Indexing;

public sealed record ExtractedSymbol(string Name, string FullName, string Kind, string? Container, string Signature, string? Doc, int StartLine, int EndLine);

public sealed record ExtractedChunk(int Ordinal, int StartLine, int EndLine, string? Heading, string Text);

public sealed record Extraction(IReadOnlyList<ExtractedSymbol> Symbols, IReadOnlyList<ExtractedChunk> Chunks, int LineCount)
{
    public static Extraction Empty { get; } = new([], [], 0);
}

/// <summary>Turns file text into symbols (for navigation) and chunks (for search) per language.</summary>
public static class Extractor
{
    public const int MaxChunkLines = 80;
    public const int ChunkOverlap = 10;
    public const int MaxChunkChars = 6000;

    public static Extraction Extract(string relativePath, string language, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = SplitLines(text);
        var extraction = language switch
        {
            "csharp" => CSharpExtractor.Extract(text, lines),
            "markdown" => MarkdownExtractor.Extract(lines),
            "typescript" or "javascript" or "python" or "go" or "rust" or "java" or "kotlin" => RegexSymbolExtractor.Extract(language, lines),
            _ => new Extraction([], WindowChunks(lines, 0), lines.Length),
        };

        // Chunk text is what agents see back; never persist secrets from config or docs.
        var chunks = extraction.Chunks.Select(c => SecretRedactor.ContainsSecret(c.Text) ? c with { Text = SecretRedactor.Redact(c.Text)! } : c).ToList();
        return extraction with { Chunks = chunks };
    }

    /// <summary>Overlapping fixed-size windows; the fallback for any text and for oversized members.</summary>
    public static List<ExtractedChunk> WindowChunks(string[] lines, int startOrdinal, int startLine = 1, int? endLine = null, string? heading = null)
    {
        var chunks = new List<ExtractedChunk>();
        var last = Math.Min(endLine ?? lines.Length, lines.Length);
        var ordinal = startOrdinal;
        var from = startLine;
        while (from <= last)
        {
            var to = Math.Min(from + MaxChunkLines - 1, last);
            var text = Join(lines, from, to);
            if (text.Trim().Length > 0)
            {
                chunks.Add(new ExtractedChunk(ordinal++, from, to, heading, Truncate(text)));
            }

            if (to >= last)
            {
                break;
            }

            from = to + 1 - ChunkOverlap;
        }

        return chunks;
    }

    public static string[] SplitLines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    public static string Join(string[] lines, int startLine, int endLine) =>
        string.Join('\n', lines.Skip(startLine - 1).Take(endLine - startLine + 1));

    public static string Truncate(string text) =>
        text.Length <= MaxChunkChars ? text : text[..MaxChunkChars];
}
