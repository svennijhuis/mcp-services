using ModelContextProtocol;

namespace McpServices.Hosting;

/// <summary>
/// Error raised by a tool that should be surfaced to the agent verbatim. The SDK converts
/// <see cref="McpException"/> into an <c>isError</c> tool result carrying the message, while other
/// exceptions are hidden behind a generic message. Use this for every validation failure.
/// </summary>
public class ToolException(string message, Exception? inner = null) : McpException(message, inner);

public static class ToolGuard
{
    public static string NotEmpty(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolException($"Parameter '{parameterName}' is required.");
        }

        return value;
    }

    public static int InRange(int value, int min, int max, string parameterName)
    {
        if (value < min || value > max)
        {
            throw new ToolException($"Parameter '{parameterName}' must be between {min} and {max} (was {value}).");
        }

        return value;
    }

    public static T OneOf<T>(string? value, string parameterName, T defaultValue) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new ToolException($"Parameter '{parameterName}' must be one of: {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
    }
}
