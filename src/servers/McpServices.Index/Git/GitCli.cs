using System.Diagnostics;
using System.Text;
using McpServices.Hosting;

namespace McpServices.Index.Git;

/// <summary>Thin wrapper over the <c>git</c> executable; every call is optional and degrades to "no git".</summary>
public sealed class GitCli
{
    private static readonly Lazy<bool> Available = new(Probe);

    public static bool IsAvailable => Available.Value;

    public static async Task<string?> TopLevelAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsAvailable || !Directory.Exists(path))
        {
            return null;
        }

        var result = await RunAsync(path, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Output.Trim() : null;
    }

    public static async Task<string?> HeadShaAsync(string root, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Output.Trim() : null;
    }

    /// <summary>Tracked plus untracked-not-ignored files, relative paths with forward slashes.</summary>
    public static async Task<IReadOnlyList<string>?> ListFilesAsync(string root, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Paths that differ from HEAD (modified, added, deleted, renamed, untracked).</summary>
    public static async Task<IReadOnlyList<string>?> ChangedPathsAsync(string root, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        var paths = new List<string>();
        var entries = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
            {
                continue;
            }

            var status = entry[..2];
            paths.Add(entry[3..]);
            if (status.Contains('R', StringComparison.Ordinal) || status.Contains('C', StringComparison.Ordinal))
            {
                // Renames are followed by the original path as a separate NUL-terminated entry.
                if (i + 1 < entries.Length)
                {
                    paths.Add(entries[++i]);
                }
            }
        }

        return paths;
    }

    /// <summary>Commit -> touched paths for the most recent commits; used for co-change analysis.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<string>>?> RecentCommitFilesAsync(string root, int maxCommits, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, ["log", $"-n{maxCommits}", "--name-only", "--no-merges", "--pretty=format:%x01"], cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        return result.Output
            .Split('\u0001', StringSplitOptions.RemoveEmptyEntries)
            .Select(block => (IReadOnlyList<string>)block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList())
            .Where(files => files.Count > 0)
            .ToList();
    }

    public static async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!IsAvailable)
        {
            return new GitResult(false, string.Empty, "git is not installed");
        }

        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in GlobalArguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in ProcessOutput.GitBackgroundHelpersOff)
        {
            info.Environment[key] = value;
        }

        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_PAGER"] = "cat";

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitResult(false, string.Empty, ex.Message);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            var exited = process.WaitForExitAsync(cts.Token);
            var stdout = ProcessOutput.DrainAsync(process.StandardOutput.BaseStream, exited, cts.Token);
            var stderr = ProcessOutput.DrainAsync(process.StandardError.BaseStream, exited, cts.Token);
            await exited.ConfigureAwait(false);
            return new GitResult(process.ExitCode == 0, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new GitResult(false, string.Empty, "git timed out");
        }
    }

    /// <summary>
    /// Options prepended to every invocation. Background helpers that git may spawn (fsmonitor daemon,
    /// pager) inherit our pipe handles on Windows and keep them open after git itself has exited,
    /// which would make reading to end-of-file hang until the timeout.
    /// </summary>
    internal static readonly string[] GlobalArguments =
    [
        "--no-pager",
        "-c", "core.quotepath=off",
        "-c", "core.fsmonitor=false",
        "-c", "core.useBuiltinFSMonitor=false",
        "-c", "maintenance.auto=false",
        "-c", "safe.directory=*",
    ];


    private static bool Probe()
    {
        try
        {
            var info = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var (key, value) in ProcessOutput.GitBackgroundHelpersOff)
            {
                info.Environment[key] = value;
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

public sealed record GitResult(bool Success, string Output, string Error);
