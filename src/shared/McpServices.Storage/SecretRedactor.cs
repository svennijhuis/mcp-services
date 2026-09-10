using System.Text.RegularExpressions;

namespace McpServices.Storage;

/// <summary>
/// Removes credentials from free text before it is persisted or put into a prompt. Pattern based
/// (well-known token prefixes, connection-string passwords, private keys, URL credentials, generic
/// key=value secrets); it errs on the side of redacting.
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "[REDACTED]";

    private static readonly Regex[] Patterns =
    [
        PrivateKeyBlock(),
        UrlCredentials(),
        ConnectionStringPassword(),
        KnownTokenPrefixes(),
        Jwt(),
        BearerHeader(),
        GenericAssignment(),
        AwsAccessKey(),
    ];

    /// <summary>Returns the text with secrets replaced by <see cref="Placeholder"/>; null stays null.</summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        foreach (var pattern in Patterns)
        {
            result = pattern.Replace(result, match =>
                match.Groups["keep"].Success
                    ? match.Groups["keep"].Value + Placeholder
                    : Placeholder);
        }

        return result;
    }

    public static bool ContainsSecret(string? text) =>
        !string.IsNullOrEmpty(text) && Patterns.Any(p => p.IsMatch(text));

    public static IReadOnlyList<string> RedactAll(IEnumerable<string>? values) =>
        values?.Select(v => Redact(v) ?? string.Empty).ToList() ?? [];

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlock();

    // scheme://user:password@host -> scheme://user:[REDACTED]@host
    [GeneratedRegex(@"(?<keep>[a-z][a-z0-9+.-]*://[^\s:/@]+:)[^\s@/]+(?=@)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    // Password=...; / pwd=... / PASSWORD: ... inside connection strings and configs
    [GeneratedRegex(@"(?<keep>\b(?:password|pwd|passwd|secret|api[_-]?key|access[_-]?key|client[_-]?secret|auth[_-]?token|private[_-]?key)\s*[=:]\s*)(?!\[REDACTED\])[^\s;,'""]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPassword();

    // OpenAI/Anthropic/GitHub/Slack/Stripe/Google/npm/Cursor style prefixes
    [GeneratedRegex(@"\b(?:sk-(?:ant-|proj-|live-|test-)?[A-Za-z0-9_\-]{16,}|ghp_[A-Za-z0-9]{30,}|gho_[A-Za-z0-9]{30,}|ghu_[A-Za-z0-9]{30,}|ghs_[A-Za-z0-9]{30,}|ghr_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|xox[abprs]-[A-Za-z0-9\-]{10,}|(?:sk|pk|rk)_(?:live|test)_[A-Za-z0-9]{16,}|AIza[0-9A-Za-z_\-]{35}|npm_[A-Za-z0-9]{36}|key_[A-Za-z0-9]{32,}|glpat-[A-Za-z0-9_\-]{20,}|ya29\.[A-Za-z0-9_\-]+)")]
    private static partial Regex KnownTokenPrefixes();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\b")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?<keep>\b(?:Bearer|Basic)\s+)(?!\[REDACTED\])[A-Za-z0-9_\-\.=+/]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerHeader();

    // TOKEN="abcd..." / api_key: 'xyz' / SECRET_KEY = value (env-style, 16+ chars, no spaces)
    [GeneratedRegex(@"(?<keep>\b[A-Z][A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|PASSWD|API_KEY|APIKEY|PRIVATE_KEY|ACCESS_KEY)\b\s*[=:]\s*['""]?)(?!\[REDACTED\])[^\s'""]{8,}")]
    private static partial Regex GenericAssignment();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();
}
