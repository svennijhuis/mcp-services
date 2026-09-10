using System.Text;
using System.Text.RegularExpressions;
using McpServices.Hosting;

namespace McpServices.Database;

public enum SqlKind
{
    Read,
    Write,
}

/// <summary>
/// Defence in depth for SQL passed by an agent: strips comments and string literals, rejects
/// multiple statements, and classifies the statement as read or write. Providers additionally
/// run reads in read-only transactions where the engine supports it.
/// </summary>
public static partial class SqlGuard
{
    private static readonly HashSet<string> ReadStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "EXPLAIN", "PRAGMA", "SHOW", "VALUES", "DESCRIBE", "DESC", "TABLE",
    };

    private static readonly HashSet<string> WriteTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "UPSERT", "CREATE", "ALTER", "DROP", "TRUNCATE",
        "GRANT", "REVOKE", "ATTACH", "DETACH", "VACUUM", "REINDEX", "COPY", "EXEC", "EXECUTE", "CALL", "DO",
        "LOCK", "SET", "RESET", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "BULK", "BACKUP", "RESTORE",
        "DBCC", "USE", "INTO",
    };

    public static SqlKind Classify(string sql)
    {
        var stripped = Strip(ToolGuard.NotEmpty(sql, "sql")).Trim();
        if (stripped.Length == 0)
        {
            throw new ToolException("The SQL statement is empty.");
        }

        var trimmed = stripped.TrimEnd(';', ' ', '\t', '\r', '\n');
        if (trimmed.Contains(';', StringComparison.Ordinal))
        {
            throw new ToolException("Only a single SQL statement is allowed per call.");
        }

        var tokens = Tokenize(trimmed);
        if (tokens.Count == 0)
        {
            throw new ToolException("The SQL statement is empty.");
        }

        var first = tokens[0];
        if (!ReadStarters.Contains(first))
        {
            return SqlKind.Write;
        }

        // PRAGMA name = value writes; PRAGMA name and PRAGMA name(arg) read.
        if (first.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('=', StringComparison.Ordinal))
        {
            return SqlKind.Write;
        }

        // A CTE or EXPLAIN can wrap a data-modifying statement (WITH x AS (...) DELETE ...; EXPLAIN ANALYZE UPDATE ...).
        // SELECT ... INTO also creates a table on SQL Server / PostgreSQL.
        foreach (var token in tokens.Skip(1))
        {
            if (WriteTokens.Contains(token))
            {
                return SqlKind.Write;
            }
        }

        return SqlKind.Read;
    }

    public static void EnsureRead(string sql)
    {
        if (Classify(sql) != SqlKind.Read)
        {
            throw new ToolException("read_query only accepts read-only statements (SELECT, WITH ... SELECT, EXPLAIN, PRAGMA, SHOW). Use write_query for modifications.");
        }
    }

    /// <summary>Removes comments and the contents of string literals so keyword scanning cannot be fooled.</summary>
    public static string Strip(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                sb.Append(' ');
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                sb.Append(' ');
                continue;
            }

            if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == close)
                    {
                        if (j + 1 < sql.Length && sql[j + 1] == close && close != ']')
                        {
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    j++;
                }

                sb.Append(c == '\'' ? "''" : "x");
                i = Math.Min(sql.Length, j + 1);
                continue;
            }

            if (c == '$' && i + 1 < sql.Length && (sql[i + 1] == '$' || char.IsLetter(sql[i + 1])))
            {
                // PostgreSQL dollar-quoted string: $tag$ ... $tag$
                var tagEnd = sql.IndexOf('$', i + 1);
                if (tagEnd > i)
                {
                    var tag = sql[i..(tagEnd + 1)];
                    var end = sql.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        sb.Append("''");
                        i = end + tag.Length;
                        continue;
                    }
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static List<string> Tokenize(string stripped) =>
        TokenRegex().Matches(stripped).Select(m => m.Value).ToList();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex TokenRegex();

    public static string ValidateIdentifier(string value, string parameterName)
    {
        ToolGuard.NotEmpty(value, parameterName);
        if (!IdentifierRegex().IsMatch(value))
        {
            throw new ToolException($"Parameter '{parameterName}' must be a plain identifier (letters, digits, underscore, optionally schema.name).");
        }

        return value;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$]*(\.[A-Za-z_][A-Za-z0-9_$]*)?$")]
    private static partial Regex IdentifierRegex();
}
