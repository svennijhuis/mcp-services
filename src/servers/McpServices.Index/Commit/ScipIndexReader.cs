using System.Globalization;
using System.Text;
using System.Text.Json;
using McpServices.Hosting;
using McpServices.Storage;

namespace McpServices.Index.Commit;

/// <summary>
/// Reads a SCIP index from <c>index.scip.json</c> or <c>index.scip</c> when an indexer has written one.
/// Symbols for paths that are not in the commit are ignored. Docstrings stay on the symbol.
/// </summary>
internal static class ScipIndexReader
{
    public static bool TryFind(string root, out string path)
    {
        foreach (var relative in new[] { "index.scip.json", Path.Combine(".scip", "index.scip.json"), "index.scip", Path.Combine(".scip", "index.scip") })
        {
            var full = Path.Combine(root, relative);
            if (File.Exists(full) && new FileInfo(full).Length > 0)
            {
                path = full;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    public static void Apply(CommitGraph graph, string scipPath, IReadOnlyDictionary<string, string> files)
    {
        List<RawSymbol> symbols;
        try
        {
            var bytes = File.ReadAllBytes(scipPath);
            symbols = IsJson(bytes) ? ReadJson(bytes) : ReadProtobuf(bytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            throw new ToolException($"SCIP index '{scipPath}' could not be read: {ex.Message}", ex);
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Path.Length > 0 && !files.ContainsKey(symbol.Path))
            {
                continue;
            }

            if (!known.Add(symbol.Key))
            {
                continue;
            }

            var text = symbol.Path.Length > 0 && files.TryGetValue(symbol.Path, out var fileText) ? fileText : symbol.Key;
            var hash = RepoIdentity.ContentHash(Encoding.UTF8.GetBytes(symbol.HasRange ? Slice(text, symbol.StartLine, symbol.EndLine) : text));
            graph.AddSymbol(new SymbolFact(
                symbol.Key,
                symbol.Key,
                symbol.Path,
                symbol.Kind,
                symbol.Name,
                symbol.StartLine,
                symbol.StartCol,
                symbol.EndLine,
                symbol.EndCol,
                hash,
                symbol.Doc,
                symbol.Name));
        }

        foreach (var symbol in symbols)
        {
            if (!known.Contains(symbol.Key))
            {
                continue;
            }

            foreach (var relation in symbol.Relations)
            {
                if (!known.Contains(relation.Target))
                {
                    continue;
                }

                if (relation.Implements)
                {
                    graph.Edges.Add(new EdgeFact(symbol.Key, relation.Target, EdgeKinds.Implements));
                }

                if (relation.References)
                {
                    graph.Edges.Add(new EdgeFact(symbol.Key, relation.Target, EdgeKinds.References));
                }
            }
        }

        var definitions = symbols.Where(s => known.Contains(s.Key) && s.HasRange).Select(s => (s.Key, s.Kind, s.StartLine, s.StartCol, s.EndLine, s.EndCol, s.Path)).ToList();
        foreach (var symbol in symbols)
        {
            foreach (var occurrence in symbol.Occurrences)
            {
                if (!known.Contains(occurrence.Symbol) || occurrence.Definition)
                {
                    continue;
                }

                graph.Occurrences.Add(new OccurrenceFact(occurrence.Symbol, occurrence.Path, occurrence.StartLine, occurrence.StartCol, occurrence.EndLine, occurrence.EndCol, OccurrenceRoles.Reference));
                var owner = SmallestContainer(definitions, occurrence);
                if (owner is null || owner == occurrence.Symbol)
                {
                    continue;
                }

                var targetKind = symbols.FirstOrDefault(s => s.Key == occurrence.Symbol)?.Kind ?? "symbol";
                var kind = targetKind is "method" or "function" or "constructor" ? EdgeKinds.Calls : EdgeKinds.References;
                graph.Edges.Add(new EdgeFact(owner, occurrence.Symbol, kind));
            }
        }
    }

    private static string? SmallestContainer(List<(string Key, string Kind, int StartLine, int StartCol, int EndLine, int EndCol, string Path)> definitions, RawOccurrence occurrence)
    {
        string? best = null;
        var bestSpan = int.MaxValue;
        foreach (var definition in definitions)
        {
            if (definition.Path != occurrence.Path || !Contains(definition, occurrence))
            {
                continue;
            }

            var span = ((definition.EndLine - definition.StartLine) * 10000) + Math.Abs(definition.EndCol - definition.StartCol);
            if (span < bestSpan)
            {
                bestSpan = span;
                best = definition.Key;
            }
        }

        return best;
    }

    private static bool Contains((string Key, string Kind, int StartLine, int StartCol, int EndLine, int EndCol, string Path) box, RawOccurrence point) =>
        (point.StartLine > box.StartLine || (point.StartLine == box.StartLine && point.StartCol >= box.StartCol))
        && (point.StartLine < box.EndLine || (point.StartLine == box.EndLine && point.StartCol <= box.EndCol));

    private static string Slice(string text, int startLine, int endLine)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length == 0)
        {
            return text;
        }

        var from = Math.Clamp(startLine, 1, lines.Length);
        var to = Math.Clamp(endLine, from, lines.Length);
        return string.Join('\n', lines.Skip(from - 1).Take(to - from + 1));
    }

    private static bool IsJson(byte[] bytes)
    {
        var i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            i = 3;
        }

        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
        {
            i++;
        }

        return i < bytes.Length && bytes[i] == (byte)'{';
    }

    private static List<RawSymbol> ReadJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var symbols = new List<RawSymbol>();
        if (root.TryGetProperty("documents", out var documents) || root.TryGetProperty("Documents", out documents))
        {
            foreach (var documentElement in documents.EnumerateArray())
            {
                ReadJsonDocument(documentElement, symbols);
            }
        }

        if (root.TryGetProperty("external_symbols", out var external) || root.TryGetProperty("externalSymbols", out external))
        {
            foreach (var element in external.EnumerateArray())
            {
                if (ReadJsonSymbol(element, string.Empty) is { } symbol)
                {
                    symbols.Add(symbol);
                }
            }
        }

        return symbols;
    }

    private static void ReadJsonDocument(JsonElement document, List<RawSymbol> symbols)
    {
        var path = StringProp(document, "relative_path", "relativePath")?.Replace('\\', '/').TrimStart('/') ?? string.Empty;
        var local = new List<RawSymbol>();
        if (document.TryGetProperty("symbols", out var symbolElements) || document.TryGetProperty("Symbols", out symbolElements))
        {
            foreach (var element in symbolElements.EnumerateArray())
            {
                if (ReadJsonSymbol(element, path) is { } symbol)
                {
                    local.Add(symbol);
                }
            }
        }

        if (document.TryGetProperty("occurrences", out var occurrences) || document.TryGetProperty("Occurrences", out occurrences))
        {
            foreach (var element in occurrences.EnumerateArray())
            {
                var key = StringProp(element, "symbol", "Symbol");
                if (string.IsNullOrWhiteSpace(key) || !element.TryGetProperty("range", out var rangeElement) && !element.TryGetProperty("Range", out rangeElement))
                {
                    continue;
                }

                var range = rangeElement.EnumerateArray().Select(item => item.GetInt32()).ToArray();
                var (startLine, startCol, endLine, endCol) = Range(range);
                var roles = IntProp(element, "symbol_roles", "symbolRoles");
                var definition = (roles & 1) != 0;
                var owner = local.FirstOrDefault(s => s.Key == key);
                if (owner is null)
                {
                    owner = new RawSymbol(key, key, "symbol", null, path);
                    local.Add(owner);
                }

                if (definition && !owner.HasRange)
                {
                    owner.HasRange = true;
                    owner.StartLine = startLine;
                    owner.StartCol = startCol;
                    owner.EndLine = endLine;
                    owner.EndCol = endCol;
                }

                owner.Occurrences.Add(new RawOccurrence(key, path, startLine, startCol, endLine, endCol, definition));
            }
        }

        symbols.AddRange(local);
    }

    private static RawSymbol? ReadJsonSymbol(JsonElement element, string path)
    {
        var key = StringProp(element, "symbol", "Symbol");
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var display = StringProp(element, "display_name", "displayName");
        var kind = "symbol";
        if (element.TryGetProperty("kind", out var kindElement) || element.TryGetProperty("Kind", out kindElement))
        {
            kind = kindElement.ValueKind switch
            {
                JsonValueKind.Number => KindName(kindElement.GetInt32()),
                JsonValueKind.String => KindName(kindElement.GetString()),
                _ => "symbol",
            };
        }

        string? doc = null;
        if (element.TryGetProperty("documentation", out var documentation) || element.TryGetProperty("Documentation", out documentation))
        {
            doc = documentation.ValueKind switch
            {
                JsonValueKind.String => documentation.GetString(),
                JsonValueKind.Array => string.Join('\n', documentation.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item))),
                _ => null,
            };
            if (doc is { Length: > 2000 })
            {
                doc = doc[..2000];
            }
        }

        var symbol = new RawSymbol(key, string.IsNullOrWhiteSpace(display) ? Display(key) : display!, kind, string.IsNullOrWhiteSpace(doc) ? null : doc, path);
        if (element.TryGetProperty("relationships", out var relationships) || element.TryGetProperty("Relationships", out relationships))
        {
            foreach (var relationship in relationships.EnumerateArray())
            {
                var target = StringProp(relationship, "symbol", "Symbol");
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                symbol.Relations.Add(new RawRelation(target, BoolProp(relationship, "is_reference", "isReference"), BoolProp(relationship, "is_implementation", "isImplementation")));
            }
        }

        return symbol;
    }

    private static List<RawSymbol> ReadProtobuf(byte[] bytes)
    {
        var reader = new ProtoReader(bytes);
        var symbols = new List<RawSymbol>();
        while (!reader.Eof)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 2 when wire == 2:
                    ReadProtoDocument(reader.ReadBytes().ToArray(), symbols);
                    break;
                case 3 when wire == 2:
                    if (ReadProtoSymbol(reader.ReadBytes().ToArray(), string.Empty) is { } external)
                    {
                        symbols.Add(external);
                    }

                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return symbols;
    }

    private static void ReadProtoDocument(byte[] bytes, List<RawSymbol> symbols)
    {
        var reader = new ProtoReader(bytes);
        var path = string.Empty;
        var local = new List<RawSymbol>();
        var occurrences = new List<byte[]>();
        while (!reader.Eof)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 2:
                    path = Encoding.UTF8.GetString(reader.ReadBytes()).Replace('\\', '/').TrimStart('/');
                    break;
                case 2 when wire == 2:
                    occurrences.Add(reader.ReadBytes().ToArray());
                    break;
                case 3 when wire == 2:
                    if (ReadProtoSymbol(reader.ReadBytes().ToArray(), string.Empty) is { } symbol)
                    {
                        local.Add(symbol);
                    }

                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        foreach (var symbol in local)
        {
            symbol.Path = path;
        }

        foreach (var occurrenceBytes in occurrences)
        {
            var occurrence = ReadProtoOccurrence(occurrenceBytes, path);
            if (occurrence is null)
            {
                continue;
            }

            var owner = local.FirstOrDefault(s => s.Key == occurrence.Symbol);
            if (owner is null)
            {
                owner = new RawSymbol(occurrence.Symbol, Display(occurrence.Symbol), "symbol", null, path);
                local.Add(owner);
            }

            if (occurrence.Definition && !owner.HasRange)
            {
                owner.HasRange = true;
                owner.StartLine = occurrence.StartLine;
                owner.StartCol = occurrence.StartCol;
                owner.EndLine = occurrence.EndLine;
                owner.EndCol = occurrence.EndCol;
            }

            owner.Occurrences.Add(occurrence);
        }

        symbols.AddRange(local);
    }

    private static RawSymbol? ReadProtoSymbol(byte[] bytes, string path)
    {
        var reader = new ProtoReader(bytes);
        var key = string.Empty;
        var docs = new List<string>();
        var kind = 0;
        var display = string.Empty;
        var relations = new List<RawRelation>();
        while (!reader.Eof)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 2:
                    key = Encoding.UTF8.GetString(reader.ReadBytes());
                    break;
                case 3 when wire == 2:
                    docs.Add(Encoding.UTF8.GetString(reader.ReadBytes()));
                    break;
                case 4 when wire == 2:
                    relations.Add(ReadProtoRelation(reader.ReadBytes().ToArray()));
                    break;
                case 5 when wire == 0:
                    kind = (int)reader.ReadVarint();
                    break;
                case 6 when wire == 2:
                    display = Encoding.UTF8.GetString(reader.ReadBytes());
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var doc = string.Join('\n', docs.Where(d => !string.IsNullOrWhiteSpace(d)));
        var symbol = new RawSymbol(key, string.IsNullOrWhiteSpace(display) ? Display(key) : display, KindName(kind), doc.Length == 0 ? null : (doc.Length > 2000 ? doc[..2000] : doc), path);
        symbol.Relations.AddRange(relations.Where(r => r.Target.Length > 0));
        return symbol;
    }

    private static RawRelation ReadProtoRelation(byte[] bytes)
    {
        var reader = new ProtoReader(bytes);
        var target = string.Empty;
        var references = false;
        var implements = false;
        while (!reader.Eof)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 2:
                    target = Encoding.UTF8.GetString(reader.ReadBytes());
                    break;
                case 2 when wire == 0:
                    references = reader.ReadVarint() != 0;
                    break;
                case 3 when wire == 0:
                    implements = reader.ReadVarint() != 0;
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new RawRelation(target, references, implements);
    }

    private static RawOccurrence? ReadProtoOccurrence(byte[] bytes, string path)
    {
        var reader = new ProtoReader(bytes);
        var range = new List<int>();
        var key = string.Empty;
        var roles = 0;
        while (!reader.Eof)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 2:
                    var packed = new ProtoReader(reader.ReadBytes().ToArray());
                    while (!packed.Eof)
                    {
                        range.Add((int)packed.ReadVarint());
                    }

                    break;
                case 1 when wire == 0:
                    range.Add((int)reader.ReadVarint());
                    break;
                case 2 when wire == 2:
                    key = Encoding.UTF8.GetString(reader.ReadBytes());
                    break;
                case 3 when wire == 0:
                    roles = (int)reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (key.Length == 0 || range.Count < 3)
        {
            return null;
        }

        var (startLine, startCol, endLine, endCol) = Range(range);
        return new RawOccurrence(key, path, startLine, startCol, endLine, endCol, (roles & 1) != 0);
    }

    private static (int StartLine, int StartCol, int EndLine, int EndCol) Range(IReadOnlyList<int> range)
    {
        var startLine = range[0] + 1;
        var startCol = range[1] + 1;
        var endLine = range.Count >= 4 ? range[2] + 1 : startLine;
        var endExclusive = range.Count >= 4 ? range[3] : range[2];
        var endCol = endExclusive <= 0 ? startCol : endExclusive;
        if (endLine < startLine)
        {
            endLine = startLine;
            endCol = startCol;
        }

        return (startLine, startCol, endLine, endCol);
    }

    private static string KindName(int kind) => kind switch
    {
        7 => "class",
        9 => "constructor",
        11 => "enum",
        12 => "enummember",
        15 => "field",
        17 => "function",
        21 => "interface",
        26 or 66 or 67 or 68 or 69 or 70 or 71 or 76 or 80 => "method",
        30 => "namespace",
        41 => "property",
        49 => "struct",
        54 => "type",
        61 => "variable",
        73 => "delegate",
        _ => "symbol",
    };

    private static string KindName(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return "symbol";
        }

        if (int.TryParse(kind, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return KindName(number);
        }

        var name = kind.Trim().ToLowerInvariant();
        return name switch
        {
            "classmethod" or "staticmethod" or "abstractmethod" => "method",
            "enummember" => "enummember",
            _ => name,
        };
    }

    private static string Display(string symbol)
    {
        var tick = symbol.LastIndexOf('`');
        if (tick >= 0 && tick < symbol.Length - 1)
        {
            var end = symbol.IndexOf('`', tick + 1);
            if (end > tick + 1)
            {
                return symbol[(tick + 1)..end];
            }
        }

        return symbol.Length <= 80 ? symbol : symbol[^80..];
    }

    private static string? StringProp(JsonElement element, string snake, string camel)
    {
        if (element.TryGetProperty(snake, out var snakeValue) && snakeValue.ValueKind == JsonValueKind.String)
        {
            return snakeValue.GetString();
        }

        if (element.TryGetProperty(camel, out var camelValue) && camelValue.ValueKind == JsonValueKind.String)
        {
            return camelValue.GetString();
        }

        return null;
    }

    private static int IntProp(JsonElement element, string snake, string camel)
    {
        if (element.TryGetProperty(snake, out var snakeValue) && snakeValue.TryGetInt32(out var snakeNumber))
        {
            return snakeNumber;
        }

        if (element.TryGetProperty(camel, out var camelValue) && camelValue.TryGetInt32(out var camelNumber))
        {
            return camelNumber;
        }

        return 0;
    }

    private static bool BoolProp(JsonElement element, string snake, string camel)
    {
        if (element.TryGetProperty(snake, out var snakeValue))
        {
            return snakeValue.ValueKind == JsonValueKind.True;
        }

        if (element.TryGetProperty(camel, out var camelValue))
        {
            return camelValue.ValueKind == JsonValueKind.True;
        }

        return false;
    }

    private sealed class RawSymbol(string key, string name, string kind, string? doc, string path)
    {
        public string Key { get; } = key;

        public string Name { get; } = name;

        public string Kind { get; } = kind;

        public string? Doc { get; } = doc;

        public string Path { get; set; } = path;

        public bool HasRange { get; set; }

        public int StartLine { get; set; } = 1;

        public int StartCol { get; set; } = 1;

        public int EndLine { get; set; } = 1;

        public int EndCol { get; set; } = 1;

        public List<RawRelation> Relations { get; } = [];

        public List<RawOccurrence> Occurrences { get; } = [];
    }

    private sealed record RawRelation(string Target, bool References, bool Implements);

    private sealed record RawOccurrence(string Symbol, string Path, int StartLine, int StartCol, int EndLine, int EndCol, bool Definition);

    private sealed class ProtoReader(byte[] bytes)
    {
        private int _pos;

        public bool Eof => _pos >= bytes.Length;

        public (int Field, int Wire) ReadTag()
        {
            var tag = ReadVarint();
            return ((int)(tag >> 3), (int)(tag & 7));
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            var shift = 0;
            while (_pos < bytes.Length)
            {
                var current = bytes[_pos++];
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }

                shift += 7;
                if (shift > 63)
                {
                    throw new InvalidDataException("SCIP varint is too long.");
                }
            }

            throw new InvalidDataException("SCIP varint ended early.");
        }

        public ReadOnlySpan<byte> ReadBytes()
        {
            var length = checked((int)ReadVarint());
            if (length < 0 || _pos + length > bytes.Length)
            {
                throw new InvalidDataException("SCIP length is out of range.");
            }

            var slice = bytes.AsSpan(_pos, length);
            _pos += length;
            return slice;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0:
                    _ = ReadVarint();
                    return;
                case 1:
                    _pos += 8;
                    return;
                case 2:
                    _ = ReadBytes();
                    return;
                case 5:
                    _pos += 4;
                    return;
                default:
                    throw new InvalidDataException($"SCIP wire type {wire} is not supported.");
            }
        }
    }
}
