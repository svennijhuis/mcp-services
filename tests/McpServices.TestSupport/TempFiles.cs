namespace McpServices.TestSupport;

/// <summary>
/// Deletion helpers for test scratch files. A server process that has just been disposed may still be
/// shutting down and, on Windows, still hold its SQLite file or working directory open for a moment.
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
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }
}
