using System.Text.RegularExpressions;

namespace McpServices.Index.Indexing;

/// <summary>One chunk per heading section; headings become symbols so docs are navigable too.</summary>
public static partial class MarkdownExtractor
{
    public static Extraction Extract(string[] lines)
    {
        var symbols = new List<ExtractedSymbol>();
        var chunks = new List<ExtractedChunk>();
        var sectionStart = 1;
        string? heading = null;
        var stack = new List<(int Level, string Title, string FullName)>();
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var match = HeadingRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            Flush(lines, chunks, sectionStart, i, heading);
            var level = match.Groups[1].Value.Length;
            var title = match.Groups[2].Value.Trim();
            while (stack.Count > 0 && stack[^1].Level >= level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            var container = stack.Count > 0 ? stack[^1].FullName : null;
            var fullName = container is null ? title : container + " > " + title;
            stack.Add((level, title, fullName));
            symbols.Add(new ExtractedSymbol(title, fullName, "heading", container, new string('#', level) + " " + title, null, i + 1, i + 1));
            heading = fullName;
            sectionStart = i + 1;
        }

        Flush(lines, chunks, sectionStart, lines.Length, heading);

        // Fix up heading symbol end lines to cover their section.
        for (var s = 0; s < symbols.Count; s++)
        {
            var end = s + 1 < symbols.Count ? symbols[s + 1].StartLine - 1 : lines.Length;
            symbols[s] = symbols[s] with { EndLine = Math.Max(symbols[s].StartLine, end) };
        }

        return new Extraction(symbols, chunks, lines.Length);
    }

    private static void Flush(string[] lines, List<ExtractedChunk> chunks, int startLine, int endLine, string? heading)
    {
        if (endLine < startLine)
        {
            return;
        }

        chunks.AddRange(Extractor.WindowChunks(lines, chunks.Count, startLine, endLine, heading));
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();
}
