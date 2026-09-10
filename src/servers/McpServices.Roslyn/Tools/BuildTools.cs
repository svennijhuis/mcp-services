using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using McpServices.Hosting;
using ModelContextProtocol.Server;

namespace McpServices.Roslyn.Tools;

[McpServerToolType]
public sealed partial class BuildTools(WorkspaceManager workspaces, RoslynOptions options)
{
    [McpServerTool(Name = "build_project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Build")]
    [Description("Run 'dotnet build' on a solution/project (default: the loaded workspace) and return structured errors and warnings. Use compile_check for a faster in-memory answer; use this when you need the real build (analyzers, source generators, MSBuild targets).")]
    public async Task<object> BuildProject(
        [Description("Path to a .sln/.slnx/.csproj; default: the loaded workspace.")] string? path = null,
        [Description("Configuration (default Debug).")] string? configuration = null,
        [Description("Skip restore (default false).")] bool noRestore = false,
        [Description("Extra MSBuild properties, e.g. ['TreatWarningsAsErrors=false'].")] string[]? properties = null,
        [Description("Timeout in seconds (default 600).")] int? timeoutSeconds = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(path, workspaceId, cancellationToken).ConfigureAwait(false);
        var args = new List<string> { "build", target, "--nologo", "-c", configuration ?? "Debug", "-v", "quiet" };
        if (noRestore)
        {
            args.Add("--no-restore");
        }

        foreach (var property in properties ?? [])
        {
            args.Add("-p:" + property);
        }

        var run = await RunDotnetAsync(args, Path.GetDirectoryName(target)!, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 600, 10, 3600)), cancellationToken).ConfigureAwait(false);
        var messages = ParseMsBuild(run.Output);
        return new
        {
            target,
            success = run.ExitCode == 0,
            run.ExitCode,
            errors = messages.Count(m => m.Severity == "error"),
            warnings = messages.Count(m => m.Severity == "warning"),
            messages = messages.Take(options.MaxResults),
            elapsedMs = run.ElapsedMs,
            timedOut = run.TimedOut,
            outputTail = run.ExitCode == 0 ? null : Tail(run.Output, 40),
        };
    }

    [McpServerTool(Name = "test_run", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Run tests")]
    [Description("Run 'dotnet test' on a solution/project (default: the loaded workspace) with an optional filter and return per-test results parsed from the TRX report: failures with messages first.")]
    public async Task<object> TestRun(
        [Description("Path to a .sln/.slnx/.csproj; default: the loaded workspace.")] string? path = null,
        [Description("Test filter expression, e.g. 'FullyQualifiedName~OrderTests' or 'Category=Unit'.")] string? filter = null,
        [Description("Configuration (default Debug).")] string? configuration = null,
        [Description("Skip the build (default false).")] bool noBuild = false,
        [Description("Timeout in seconds (default 900).")] int? timeoutSeconds = null,
        [Description("Maximum passed tests to list (failures are always listed; default 50).")] int? maxPassed = null,
        [Description(WorkspaceTools.WorkspaceIdDescription)] string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(path, workspaceId, cancellationToken).ConfigureAwait(false);
        var resultsDirectory = Path.Combine(Path.GetTempPath(), "mcp-roslyn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resultsDirectory);
        var args = new List<string> { "test", target, "--nologo", "-c", configuration ?? "Debug", "-v", "quiet", "--logger", "trx", "--results-directory", resultsDirectory };
        if (noBuild)
        {
            args.Add("--no-build");
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.Add("--filter");
            args.Add(filter);
        }

        try
        {
            var run = await RunDotnetAsync(args, Path.GetDirectoryName(target)!, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 900, 10, 7200)), cancellationToken).ConfigureAwait(false);
            var tests = Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories).SelectMany(ParseTrx).ToList();
            var buildMessages = ParseMsBuild(run.Output).Where(m => m.Severity == "error").ToList();
            var failed = tests.Where(t => t.Outcome is "failed").ToList();
            var passedCap = Math.Clamp(maxPassed ?? 50, 0, 2000);
            return new
            {
                target,
                success = run.ExitCode == 0 && failed.Count == 0 && !run.TimedOut,
                run.ExitCode,
                summary = new
                {
                    total = tests.Count,
                    passed = tests.Count(t => t.Outcome == "passed"),
                    failed = failed.Count,
                    skipped = tests.Count(t => t.Outcome is "skipped" or "notexecuted"),
                    durationMs = tests.Sum(t => t.DurationMs),
                },
                failures = failed.Take(options.MaxResults),
                passed = tests.Where(t => t.Outcome == "passed").Take(passedCap).Select(t => new { t.Name, t.DurationMs }),
                buildErrors = buildMessages.Take(50),
                elapsedMs = run.ElapsedMs,
                timedOut = run.TimedOut,
                outputTail = tests.Count == 0 || run.ExitCode != 0 ? Tail(run.Output, 40) : null,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(resultsDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Leftover temp files are harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    private async Task<string> ResolveTargetAsync(string? path, string? workspaceId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var full = Path.GetFullPath(path);
            options.EnsureAllowed(full);
            if (Directory.Exists(full))
            {
                return full;
            }

            if (!File.Exists(full))
            {
                throw new ToolException($"'{path}' does not exist.");
            }

            return full;
        }

        var session = await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return session.Path;
    }

    internal static async Task<ProcessRun> RunDotnetAsync(IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(DotnetPath())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";
        startInfo.Environment["NUGET_XMLDOC_MODE"] = "skip";

        var output = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ToolException($"Cannot start 'dotnet': {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            if (!timedOut)
            {
                throw;
            }
        }

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        return new ProcessRun(timedOut ? -1 : process.ExitCode, text, stopwatch.ElapsedMilliseconds, timedOut);
    }

    internal static string DotnetPath()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
        {
            return host;
        }

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }

    public static IReadOnlyList<BuildMessage> ParseMsBuild(string output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<BuildMessage>();
        foreach (var line in output.Split('\n'))
        {
            var match = MsBuildLine().Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            var message = new BuildMessage(
                match.Groups["severity"].Value.ToLowerInvariant(),
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim(),
                match.Groups["file"].Success ? match.Groups["file"].Value : null,
                match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture) : null,
                match.Groups["column"].Success ? int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture) : null,
                match.Groups["project"].Success ? match.Groups["project"].Value : null);
            if (seen.Add($"{message.Severity}|{message.Code}|{message.File}|{message.Line}|{message.Message}"))
            {
                messages.Add(message);
            }
        }

        return messages.OrderBy(m => m.Severity == "error" ? 0 : 1).ThenBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line).ToList();
    }

    public static IEnumerable<TestResult> ParseTrx(string path)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }

        XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
        foreach (var result in document.Descendants(ns + "UnitTestResult"))
        {
            var name = result.Attribute("testName")?.Value ?? "?";
            var outcome = (result.Attribute("outcome")?.Value ?? "unknown").ToLowerInvariant();
            var duration = result.Attribute("duration")?.Value;
            var durationMs = duration is not null && TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var span) ? (long)span.TotalMilliseconds : 0;
            var error = result.Element(ns + "Output")?.Element(ns + "ErrorInfo");
            yield return new TestResult(
                name,
                outcome,
                durationMs,
                error?.Element(ns + "Message")?.Value.Trim(),
                error?.Element(ns + "StackTrace")?.Value.Trim() is { } trace ? string.Join('\n', trace.Split('\n').Take(12)) : null);
        }
    }

    private static string Tail(string output, int lines)
    {
        var all = output.Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)).Select(l => l.TrimEnd('\r')));
    }

    [GeneratedRegex(@"^(?:(?<file>[^(\s][^(]*?)\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\):\s*)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<message>.*?)(?:\s+\[(?<project>[^\]]+)\])?\s*$")]
    private static partial Regex MsBuildLine();

    public sealed record ProcessRun(int ExitCode, string Output, long ElapsedMs, bool TimedOut);

    public sealed record BuildMessage(string Severity, string Code, string Message, string? File, int? Line, int? Column, string? Project);

    public sealed record TestResult(string Name, string Outcome, long DurationMs, string? Error, string? StackTrace);
}
