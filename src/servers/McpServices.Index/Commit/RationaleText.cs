using McpServices.Hosting;

namespace McpServices.Index.Commit;

/// <summary>
/// Rules the rationale table also enforces: 500 characters, no markdown heading, required anchor,
/// confidence and source. The database rejects a row that breaks them.
/// </summary>
internal static class RationaleText
{
    public const int MaxCharacters = 500;

    public static string Normalize(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (normalized.Length is < 1 or > MaxCharacters)
        {
            throw new ToolException($"why text must be 1 to {MaxCharacters} characters.");
        }

        if (HasHeading(normalized))
        {
            throw new ToolException("why text cannot contain a markdown heading.");
        }

        return normalized;
    }

    public static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolException($"Parameter '{parameterName}' is required.");
        }

        return value.Trim();
    }

    public static bool HasHeading(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var spaces = 0;
            while (spaces < line.Length && spaces < 4 && line[spaces] == ' ')
            {
                spaces++;
            }

            if (spaces <= 3 && spaces < line.Length && line[spaces] == '#')
            {
                return true;
            }
        }

        return false;
    }
}
