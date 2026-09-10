namespace McpServices.FileSystem;

public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".pdf"] = "application/pdf",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".zip"] = "application/zip",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".cs"] = "text/x-csharp",
        [".js"] = "text/javascript",
        [".ts"] = "text/typescript",
        [".html"] = "text/html",
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".yml"] = "application/yaml",
        [".yaml"] = "application/yaml",
    };

    public static string FromPath(string path) =>
        Map.TryGetValue(Path.GetExtension(path), out var mime) ? mime : "application/octet-stream";
}
