using System.Text;
using System.Text.RegularExpressions;

namespace McpServices.Storage;

/// <summary>
/// The handful of SQL differences between SQLite and PostgreSQL that server code needs. Everything
/// else (tables, joins, parameters as <c>@name</c>) is written once and works on both engines.
/// </summary>
public abstract partial class SqlDialect
{
    public static SqlDialect Sqlite { get; } = new SqliteDialect();

    public static SqlDialect Postgres { get; } = new PostgresDialect();

    public abstract StoreKind Kind { get; }

    /// <summary>Current time as unix milliseconds; all timestamps are stored as bigint for parity.</summary>
    public abstract string NowMs { get; }

    /// <summary>Type name for an auto-increment integer primary key column.</summary>
    public abstract string AutoIncrementPrimaryKey { get; }

    /// <summary>Type name for binary data.</summary>
    public abstract string Blob { get; }

    /// <summary>Emits <c>ON CONFLICT (cols) DO UPDATE SET a = excluded.a, ...</c>.</summary>
    public string Upsert(string conflictColumns, params string[] updateColumns) =>
        updateColumns.Length == 0
            ? $"ON CONFLICT ({conflictColumns}) DO NOTHING"
            : $"ON CONFLICT ({conflictColumns}) DO UPDATE SET {string.Join(", ", updateColumns.Select(c => $"{c} = excluded.{c}"))}";

    /// <summary>Case-insensitive LIKE.</summary>
    public abstract string ILike(string column, string parameter);

    /// <summary>
    /// Turns free text into an engine query string for full-text search: SQLite FTS5 MATCH syntax
    /// or a <c>to_tsquery</c> string. Terms are OR-ed (ranking rewards multiple matches) and the last
    /// term is prefix-matched so partially typed identifiers still hit.
    /// </summary>
    public abstract string FullTextQuery(string userQuery);

    /// <summary>Splits identifiers (<c>GetUserToken</c>, <c>user_token</c>) into lower-case words so BM25 matches partial names.</summary>
    public static string SplitIdentifiers(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length * 2);
        foreach (var token in TokenRegex().Matches(text).Select(m => m.Value))
        {
            var parts = CamelRegex().Split(token).Where(p => p.Length > 0).ToList();
            if (parts.Count > 1)
            {
                sb.Append(token).Append(' ');
                foreach (var part in parts)
                {
                    sb.Append(part.ToLowerInvariant()).Append(' ');
                }
            }
            else
            {
                sb.Append(token.ToLowerInvariant()).Append(' ');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being", "do", "does", "did", "have", "has", "had",
        "where", "what", "which", "who", "whom", "how", "when", "why", "in", "on", "of", "to", "for", "and", "or", "not",
        "with", "by", "at", "from", "this", "that", "these", "those", "it", "its", "as", "i", "we", "you", "my", "our", "your",
        "can", "could", "should", "would", "will", "into", "about", "there", "here", "any", "some", "all", "me", "us", "them",
    };

    /// <summary>Lower-cased query terms without stop words; if everything was a stop word, keep the original terms.</summary>
    protected static IReadOnlyList<string> Terms(string userQuery)
    {
        var all = TokenRegex().Matches(userQuery ?? string.Empty).Select(m => m.Value.ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        var meaningful = all.Where(t => !StopWords.Contains(t)).ToList();
        return meaningful.Count > 0 ? meaningful : all;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"_|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])")]
    private static partial Regex CamelRegex();

    private sealed class SqliteDialect : SqlDialect
    {
        public override StoreKind Kind => StoreKind.Sqlite;

        public override string NowMs => "CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)";

        public override string AutoIncrementPrimaryKey => "INTEGER PRIMARY KEY AUTOINCREMENT";

        public override string Blob => "BLOB";

        public override string ILike(string column, string parameter) => $"{column} LIKE {parameter}";

        public override string FullTextQuery(string userQuery)
        {
            var terms = Terms(userQuery);
            if (terms.Count == 0)
            {
                return "\"\"";
            }

            // Quote every term so FTS5 operators in user input (AND, OR, NOT, *, :) cannot break the query.
            // Terms are OR-ed: bm25 ranks rows matching more terms higher, and natural-language queries still hit.
            var quoted = terms.Select((t, i) => i == terms.Count - 1 ? $"\"{t}\"*" : $"\"{t}\"");
            return string.Join(" OR ", quoted);
        }
    }

    private sealed class PostgresDialect : SqlDialect
    {
        public override StoreKind Kind => StoreKind.Postgres;

        public override string NowMs => "(EXTRACT(EPOCH FROM clock_timestamp()) * 1000)::bigint";

        public override string AutoIncrementPrimaryKey => "BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY";

        public override string Blob => "BYTEA";

        public override string ILike(string column, string parameter) => $"{column} ILIKE {parameter}";

        public override string FullTextQuery(string userQuery)
        {
            // Consumed by to_tsquery: "a | b | c:*".
            var terms = Terms(userQuery);
            if (terms.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(" | ", terms.Select((t, i) => i == terms.Count - 1 ? $"{t}:*" : t));
        }
    }
}
