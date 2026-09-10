using System.Diagnostics;
using McpServices.TestSupport;

namespace McpServices.Roslyn.Tests;

/// <summary>
/// Copies the sample solution to a temp directory, restores it once, and starts mcp-roslyn with that
/// directory as root. Refactoring tests that write files therefore never touch the repository.
/// </summary>
public sealed class SampleSolutionFixture : IAsyncLifetime
{
    public string Root { get; private set; } = string.Empty;

    public string SolutionPath => Path.Combine(Root, "SampleSolution.sln");

    public string CalculatorFile => Path.Combine(Root, "src", "Sample.Lib", "Calculator.cs");

    public string OrderServiceFile => Path.Combine(Root, "src", "Sample.Lib", "Orders", "OrderService.cs");

    public string ProgramFile => Path.Combine(Root, "src", "Sample.App", "Program.cs");

    public ServerFixture Server { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Root = Path.Combine(Path.GetTempPath(), "mcp-roslyn-tests", Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "fixtures", "SampleSolution"), Root);
        await RestoreAsync();
        Server = await ServerFixture.StartAsync("McpServices.Roslyn", ["--root", Root, "--restrict"]);
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    public static string DotnetPath()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(host) && File.Exists(host) ? host : "dotnet";
    }

    private async Task RestoreAsync()
    {
        var info = new ProcessStartInfo(DotnetPath())
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("restore");
        info.ArgumentList.Add(SolutionPath);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["MSBUILDTERMINALLOGGER"] = "off";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start dotnet restore.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet restore of the sample solution failed ({process.ExitCode}):\n{output}\n{error}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SampleSolutionTests : ICollectionFixture<SampleSolutionFixture>
{
    public const string Name = "sample-solution";
}
