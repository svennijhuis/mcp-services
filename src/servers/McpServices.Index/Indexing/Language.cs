namespace McpServices.Index.Indexing;

public static class Language
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".csx"] = "csharp", [".razor"] = "razor", [".cshtml"] = "razor",
        [".fs"] = "fsharp", [".fsx"] = "fsharp", [".vb"] = "vb",
        [".csproj"] = "msbuild", [".fsproj"] = "msbuild", [".vbproj"] = "msbuild", [".props"] = "msbuild", [".targets"] = "msbuild", [".sln"] = "sln", [".slnx"] = "msbuild",
        [".ts"] = "typescript", [".tsx"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
        [".js"] = "javascript", [".jsx"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".py"] = "python", [".rb"] = "ruby", [".go"] = "go", [".rs"] = "rust", [".java"] = "java", [".kt"] = "kotlin", [".swift"] = "swift", [".php"] = "php",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".hpp"] = "cpp", [".cc"] = "cpp",
        [".sql"] = "sql", [".sh"] = "shell", [".bash"] = "shell", [".zsh"] = "shell", [".ps1"] = "powershell", [".psm1"] = "powershell", [".bat"] = "batch", [".cmd"] = "batch",
        [".md"] = "markdown", [".mdx"] = "markdown", [".markdown"] = "markdown", [".rst"] = "text", [".txt"] = "text", [".adoc"] = "text",
        [".json"] = "json", [".jsonc"] = "json", [".yaml"] = "yaml", [".yml"] = "yaml", [".toml"] = "toml", [".xml"] = "xml", [".config"] = "xml", [".ini"] = "ini", [".editorconfig"] = "ini", [".env.example"] = "ini",
        [".html"] = "html", [".htm"] = "html", [".css"] = "css", [".scss"] = "css", [".less"] = "css", [".vue"] = "vue", [".svelte"] = "svelte",
        [".graphql"] = "graphql", [".proto"] = "protobuf", [".tf"] = "terraform", [".dockerfile"] = "dockerfile", [".gradle"] = "gradle", [".cmake"] = "cmake", [".mk"] = "makefile",
    };

    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile", ["Makefile"] = "makefile", ["CMakeLists.txt"] = "cmake", ["Jenkinsfile"] = "groovy", ["LICENSE"] = "text", ["README"] = "text", [".gitignore"] = "ini", [".gitattributes"] = "ini", [".mcpindexignore"] = "ini",
    };

    public static string Detect(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (ByFileName.TryGetValue(name, out var byName))
        {
            return byName;
        }

        var extension = Path.GetExtension(name);
        return ByExtension.TryGetValue(extension, out var byExtension) ? byExtension : "text";
    }

    /// <summary>Text-like files are indexed; anything else is only tracked by hash.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> sample)
    {
        var limit = Math.Min(sample.Length, 8000);
        for (var i = 0; i < limit; i++)
        {
            if (sample[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
