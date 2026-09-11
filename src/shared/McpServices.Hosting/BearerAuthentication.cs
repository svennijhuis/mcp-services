using System.Security.Cryptography;
using System.Text;

namespace McpServices.Hosting;

/// <summary>Constant-time comparison of an <c>Authorization: Bearer</c> header against a shared secret.</summary>
public static class BearerAuthentication
{
    public const string HeaderScheme = "Bearer ";
    public const string Challenge = "Bearer";

    public static bool Matches(string? authorizationHeader, string expectedToken)
    {
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return false;
        }

        if (!authorizationHeader.StartsWith(HeaderScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = authorizationHeader[HeaderScheme.Length..].Trim();
        return FixedEquals(provided, expectedToken);
    }

    public static bool FixedEquals(string provided, string expected)
    {
        ArgumentNullException.ThrowIfNull(provided);
        ArgumentNullException.ThrowIfNull(expected);

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (providedBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
