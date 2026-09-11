namespace McpServices.TestSupport;

/// <summary>
/// Deletion helpers for test scratch files. A server process that has just been disposed may still be
/// shutting down and, on Windows, still hold its SQLite file or working directory open for a moment.
/// Git also marks objects read-only, which makes <see cref="Directory.Delete"/> throw
/// <see cref="UnauthorizedAccessException"/> instead of <see cref="IOException"/>.
/// </summary>
public static class TempFiles
{
    public static async Task DeleteAsync(string path, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                ClearReadOnly(path);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    public static void ClearReadOnly(string path)
    {
        if (File.Exists(path))
        {
            TryClear(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        TryClear(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            ClearReadOnly(child);
        }
    }

    private static void TryClear(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
