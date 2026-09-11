using System.Text;

namespace McpServices.Hosting;

/// <summary>
/// Helpers for reading redirected output of child processes without depending on end-of-file.
/// On Windows, helper processes that a child spawns (git's fsmonitor daemon, MSBuild worker nodes,
/// pagers) inherit the pipe handles and can keep them open after the child itself has exited, which
/// makes <c>ReadToEndAsync</c> wait forever.
/// </summary>
public static class ProcessOutput
{
    /// <summary>Default grace period after the process exits during which trailing output is still collected.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads <paramref name="stream"/> until end-of-file or, once <paramref name="exited"/> has completed,
    /// until no more data arrives within the grace period. Everything the child wrote is already in the
    /// pipe when it exits, so its own output is never truncated; only orphaned handles stop being waited on.
    /// </summary>
    public static async Task<string> DrainAsync(Stream stream, Task exited, CancellationToken cancellationToken, TimeSpan? grace = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(exited);

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = stream.ReadAsync(chunk, cancellationToken).AsTask();
            if (await Task.WhenAny(read, exited).ConfigureAwait(false) == exited && !read.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delay = Task.Delay(grace ?? DefaultGrace, cancellationToken);
                if (await Task.WhenAny(read, delay).ConfigureAwait(false) == delay)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    break;
                }
            }

            var count = await read.ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, count);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Environment variables that stop git from spawning background helpers (fsmonitor daemon,
    /// auto maintenance) for the given process and everything it launches. Uses the
    /// <c>GIT_CONFIG_COUNT</c> mechanism so it also applies to tools that call git internally.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GitBackgroundHelpersOff { get; } = new Dictionary<string, string>
    {
        ["GIT_CONFIG_COUNT"] = "3",
        ["GIT_CONFIG_KEY_0"] = "core.fsmonitor",
        ["GIT_CONFIG_VALUE_0"] = "false",
        ["GIT_CONFIG_KEY_1"] = "core.useBuiltinFSMonitor",
        ["GIT_CONFIG_VALUE_1"] = "false",
        ["GIT_CONFIG_KEY_2"] = "maintenance.auto",
        ["GIT_CONFIG_VALUE_2"] = "false",
        ["GIT_TERMINAL_PROMPT"] = "0",
    };
}
