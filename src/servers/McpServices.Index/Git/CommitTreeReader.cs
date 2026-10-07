using System.Text;

namespace McpServices.Index.Git;

/// <summary>Bytes of tracked files at one commit. The worktree is not read, so uncommitted edits stay out.</summary>
public static class CommitTreeReader
{
    public static async Task<CommitTree> ReadAsync(string root, string sha, CancellationToken cancellationToken)
    {
        var listed = await GitCli.RunAsync(root, ["ls-tree", "-r", "-z", "--name-only", sha], cancellationToken).ConfigureAwait(false);
        if (!listed.Success)
        {
            return new CommitTree(false, string.IsNullOrWhiteSpace(listed.Error) ? "git ls-tree failed" : listed.Error.Trim(), []);
        }

        var paths = listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0)
        {
            return new CommitTree(true, null, []);
        }

        var files = await CatAsync(root, sha, paths, cancellationToken).ConfigureAwait(false);
        return new CommitTree(true, null, files);
    }

    private static async Task<IReadOnlyList<CommitFileBytes>> CatAsync(string root, string sha, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = null,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in GitCli.GlobalArguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add("cat-file");
        info.ArgumentList.Add("--batch");
        foreach (var (key, value) in McpServices.Hosting.ProcessOutput.GitBackgroundHelpersOff)
        {
            info.Environment[key] = value;
        }

        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_PAGER"] = string.Empty;

        using var process = new System.Diagnostics.Process { StartInfo = info };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var reader = new ByteReader(process.StandardOutput.BaseStream);
        var read = ReadAllAsync(reader, paths.Count, cancellationToken);
        try
        {
            foreach (var path in paths)
            {
                await process.StandardInput.WriteLineAsync($"{sha}:{path}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            var files = await read.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            var results = new List<CommitFileBytes>(paths.Count);
            for (var i = 0; i < paths.Count; i++)
            {
                if (files[i] is { } bytes)
                {
                    results.Add(new CommitFileBytes(paths[i].Replace('\\', '/'), bytes));
                }
            }

            return results;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private static async Task<byte[]?[]> ReadAllAsync(ByteReader reader, int count, CancellationToken cancellationToken)
    {
        var files = new byte[]?[count];
        for (var i = 0; i < count; i++)
        {
            var header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("git cat-file ended early.");
            if (header.EndsWith(" missing", StringComparison.Ordinal))
            {
                files[i] = null;
                continue;
            }

            var space = header.LastIndexOf(' ');
            if (space < 0 || !int.TryParse(header[(space + 1)..], out var size) || size < 0)
            {
                throw new InvalidOperationException("git cat-file returned an unexpected header.");
            }

            var bytes = new byte[size];
            await reader.ReadExactAsync(bytes, cancellationToken).ConfigureAwait(false);
            await reader.ReadExactAsync(new byte[1], cancellationToken).ConfigureAwait(false);
            files[i] = bytes;
        }

        return files;
    }

    private sealed class ByteReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _pos;
        private int _len;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            while (true)
            {
                var next = await NextAsync(cancellationToken).ConfigureAwait(false);
                if (next is null)
                {
                    return builder.Length == 0 ? null : builder.ToString();
                }

                if (next.Value == '\n')
                {
                    return builder.ToString();
                }

                builder.Append((char)next.Value);
            }
        }

        public async Task ReadExactAsync(byte[] dest, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < dest.Length)
            {
                if (_pos >= _len)
                {
                    _len = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _pos = 0;
                    if (_len == 0)
                    {
                        throw new EndOfStreamException("git cat-file ended early.");
                    }
                }

                var n = Math.Min(_len - _pos, dest.Length - read);
                Buffer.BlockCopy(_buffer, _pos, dest, read, n);
                _pos += n;
                read += n;
            }
        }

        private async Task<byte?> NextAsync(CancellationToken cancellationToken)
        {
            if (_pos >= _len)
            {
                _len = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0)
                {
                    return null;
                }
            }

            return _buffer[_pos++];
        }
    }
}

public sealed record CommitTree(bool Success, string? Error, IReadOnlyList<CommitFileBytes> Files);

public sealed record CommitFileBytes(string Path, byte[] Bytes);
