using System.Text.RegularExpressions;

namespace McpServices.Index.Indexing;

/// <summary>
/// Good-enough declarations for non-.NET languages: a line-based regex per language finds
/// functions, classes and exported constants; chunks are line windows.
/// </summary>
public static partial class RegexSymbolExtractor
{
    public static Extraction Extract(string language, string[] lines)
    {
        var regex = language switch
        {
            "typescript" or "javascript" => TypeScript(),
            "python" => Python(),
            "go" => Go(),
            "rust" => Rust(),
            "java" or "kotlin" => Java(),
            _ => null,
        };

        var symbols = new List<ExtractedSymbol>();
        if (regex is not null)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var match = regex.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                var kind = match.Groups["kind"].Value.ToLowerInvariant() switch
                {
                    "class" or "struct" or "interface" or "enum" or "trait" or "impl" or "type" or "object" or "record" => match.Groups["kind"].Value.ToLowerInvariant(),
                    "def" or "fn" or "func" or "function" or "fun" => "function",
                    "const" or "let" or "var" or "val" => "constant",
                    _ => "symbol",
                };
                var end = FindBlockEnd(lines, i, language);
                symbols.Add(new ExtractedSymbol(name, name, kind, null, lines[i].Trim(), null, i + 1, end));
            }
        }

        return new Extraction(symbols, Extractor.WindowChunks(lines, 0), lines.Length);
    }

    private static int FindBlockEnd(string[] lines, int start, string language)
    {
        if (language == "python")
        {
            var indent = Indent(lines[start]);
            for (var i = start + 1; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length > 0 && Indent(lines[i]) <= indent)
                {
                    return i;
                }
            }

            return lines.Length;
        }

        var depth = 0;
        var seenBrace = false;
        for (var i = start; i < lines.Length && i < start + 2000; i++)
        {
            foreach (var c in lines[i])
            {
                if (c == '{')
                {
                    depth++;
                    seenBrace = true;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }

            if (seenBrace && depth <= 0)
            {
                return i + 1;
            }

            if (!seenBrace && i > start + 2)
            {
                return start + 1;
            }
        }

        return Math.Min(lines.Length, start + 1);
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?(?<kind>function|class|interface|type|enum|const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)")]
    private static partial Regex TypeScript();

    [GeneratedRegex(@"^\s*(?:async\s+)?(?<kind>def|class)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex Python();

    [GeneratedRegex(@"^\s*(?<kind>func|type)\s+(?:\([^)]*\)\s*)?(?<name>[A-Za-z_]\w*)")]
    private static partial Regex Go();

    [GeneratedRegex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?(?:unsafe\s+)?(?<kind>fn|struct|enum|trait|impl|type|const|static)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex Rust();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|final|abstract|open|data|sealed|override|suspend)\s+)*(?<kind>class|interface|enum|record|object|fun)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex Java();
}
