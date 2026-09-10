using Microsoft.Extensions.FileSystemGlobbing;

namespace McpServices.Index.Indexing;

/// <summary>
/// Which files never enter the index: fixed build/dependency folders, secret-bearing files and
/// user patterns from <c>.mcpindexignore</c> (gitignore-like globs, one per line, <c>!</c> negates).
/// Git's own ignore rules are honoured through <c>git ls-files</c>; without git a
/// simplified <c>.gitignore</c> reading is applied as well.
/// </summary>
public sealed class IgnoreRules
{
    public const string FileName = ".mcpindexignore";

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "bin", "obj", "node_modules", ".vs", ".idea", ".vscode", "dist", "build", "out", "target",
        "packages", "TestResults", ".next", ".nuxt", ".cache", ".terraform", "__pycache__", ".venv", "venv", ".tox", ".mypy_cache", ".pytest_cache", "coverage", ".gradle", ".dart_tool", "Pods", "DerivedData", "vendor",
    };

    private static readonly string[] SecretFilePatterns =
    [
        ".env", ".env.*", "*.pem", "*.pfx", "*.p12", "*.key", "*.jks", "*.keystore", "id_rsa", "id_rsa.*", "id_ed25519", "id_ed25519.*", "*.crt", "*.cer", "*.der", "secrets.json", "*.secrets.json", ".npmrc", ".pypirc", ".netrc", "*.kdbx", "*.tfstate", "*.tfstate.*", "credentials", "credentials.json",
    ];

    private static readonly string[] NoiseFilePatterns =
    [
        "*.min.js", "*.min.css", "*.map", "*.lock", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "*.snap", "*.dll", "*.exe", "*.pdb", "*.so", "*.dylib", "*.nupkg", "*.zip", "*.gz", "*.tar", "*.7z", "*.rar", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.ico", "*.svg", "*.webp", "*.bmp", "*.pdf", "*.woff", "*.woff2", "*.ttf", "*.eot", "*.mp3", "*.mp4", "*.mov", "*.wav", "*.db", "*.sqlite", "*.sqlite3", "*.bin", "*.dat", "*.class", "*.jar", "*.pyc", "*.o", "*.a", "*.lib", "*.obj", "*.wasm",
    ];

    private readonly Matcher _fixed = new(StringComparison.OrdinalIgnoreCase);
    private readonly Matcher? _user;
    private readonly Matcher? _userNegations;

    private IgnoreRules(IReadOnlyList<string> userPatterns)
    {
        foreach (var pattern in SecretFilePatterns.Concat(NoiseFilePatterns))
        {
            _fixed.AddInclude("**/" + pattern);
        }

        var includes = new List<string>();
        var negations = new List<string>();
        foreach (var raw in userPatterns)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var negate = line.StartsWith('!');
            if (negate)
            {
                line = line[1..];
            }

            (negate ? negations : includes).AddRange(ToGlobs(line));
        }

        if (includes.Count > 0)
        {
            _user = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var include in includes)
            {
                _user.AddInclude(include);
            }
        }

        if (negations.Count > 0)
        {
            _userNegations = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var negation in negations)
            {
                _userNegations.AddInclude(negation);
            }
        }
    }

    public static IgnoreRules Load(string root, bool includeGitignore)
    {
        var patterns = new List<string>();
        var custom = Path.Combine(root, FileName);
        if (File.Exists(custom))
        {
            patterns.AddRange(File.ReadAllLines(custom));
        }

        if (includeGitignore)
        {
            var gitignore = Path.Combine(root, ".gitignore");
            if (File.Exists(gitignore))
            {
                patterns.AddRange(File.ReadAllLines(gitignore));
            }
        }

        return new IgnoreRules(patterns);
    }

    public static bool IsExcludedDirectory(string name) => ExcludedDirectories.Contains(name);

    /// <summary><paramref name="relativePath"/> uses forward slashes.</summary>
    public bool IsIgnored(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedDirectories.Contains(segments[i]))
            {
                return true;
            }
        }

        if (_fixed.Match(relativePath).HasMatches)
        {
            return true;
        }

        if (_user is not null && _user.Match(relativePath).HasMatches)
        {
            return _userNegations is null || !_userNegations.Match(relativePath).HasMatches;
        }

        return false;
    }

    private static IEnumerable<string> ToGlobs(string pattern)
    {
        var anchored = pattern.StartsWith('/');
        pattern = pattern.TrimStart('/');
        var directoryOnly = pattern.EndsWith('/');
        pattern = pattern.TrimEnd('/');
        if (pattern.Length == 0)
        {
            yield break;
        }

        var prefix = anchored || pattern.Contains('/', StringComparison.Ordinal) ? string.Empty : "**/";
        // "foo" matches a file or a directory named foo (and everything below it).
        yield return prefix + pattern + "/**";
        if (!directoryOnly)
        {
            yield return prefix + pattern;
        }
    }
}
