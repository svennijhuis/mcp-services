using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using McpServices.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using ModelContextProtocol.Server;

namespace McpServices.Roslyn.Tools;

[McpServerToolType]
public sealed class ScriptingTools
{
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    private static readonly string[] DefaultImports =
    [
        "System", "System.IO", "System.Linq", "System.Text", "System.Text.Json", "System.Text.RegularExpressions",
        "System.Collections.Generic", "System.Threading.Tasks", "System.Globalization", "System.Net.Http",
    ];

    [McpServerTool(Name = "run_script", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, Title = "Run C# script")]
    [Description("Evaluate a C# script (csx-style: statements and expressions, no Main) in the server process with the .NET runtime referenced. Returns the value of the last expression, captured console output and compile errors. The script has the same permissions as the server; disable with --no-scripting.")]
    public async Task<object> RunScript(
        [Description("Script source. The last expression's value is returned, e.g. 'var x = 2; x * 21'.")] string code,
        [Description("Timeout in seconds (default 30, max 300).")] int? timeoutSeconds = null,
        [Description("Extra namespaces to import (System, System.Linq, System.Collections.Generic, System.Text, System.IO, System.Threading.Tasks and a few more are imported by default).")] string[]? imports = null,
        [Description("Extra assembly references: full paths to .dll files.")] string[]? references = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(code, "code");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 30, 1, 300));
        var options = ScriptOptions.Default
            .WithReferences(WorkspaceManager.RuntimeReferences.Value)
            .WithImports(DefaultImports.Concat(imports ?? []).Distinct(StringComparer.Ordinal))
            .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)
            .WithEmitDebugInformation(false)
            .WithAllowUnsafe(false);

        foreach (var reference in references ?? [])
        {
            if (!File.Exists(reference))
            {
                throw new ToolException($"Reference '{reference}' does not exist.");
            }

            options = options.AddReferences(MetadataReference.CreateFromFile(reference));
        }

        var script = CSharpScript.Create(code, options);
        var compileDiagnostics = script.Compile(cancellationToken);
        var errors = compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            return new
            {
                success = false,
                stage = "compile",
                errors = errors.Select(d => new { d.Id, message = d.GetMessage(), line = d.Location.GetLineSpan().StartLinePosition.Line + 1 }).ToList(),
            };
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var output = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        await ConsoleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previous = Console.Out;
        try
        {
            // Stdout of this process is the MCP protocol stream when running over stdio; McpServerHost already
            // redirected Console.Out to stderr, so capturing here is safe either way.
            using var writer = new StringWriter(output);
            Console.SetOut(writer);
            var run = script.RunAsync(cancellationToken: timeoutSource.Token);
            var completed = await Task.WhenAny(run, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            if (completed != run)
            {
                await timeoutSource.CancelAsync().ConfigureAwait(false);
                return new { success = false, stage = "run", error = $"Script did not finish within {timeout.TotalSeconds:0}s.", output = Trim(output), elapsedMs = stopwatch.ElapsedMilliseconds };
            }

            var state = await run.ConfigureAwait(false);
            return new
            {
                success = true,
                returnValue = Render(state.ReturnValue),
                returnType = state.ReturnValue?.GetType().FullName,
                output = Trim(output),
                variables = state.Variables.Select(v => new { v.Name, type = v.Type.Name, value = Render(v.Value) }).Take(50).ToList(),
                elapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (CompilationErrorException ex)
        {
            return new { success = false, stage = "compile", errors = ex.Diagnostics.Select(d => new { d.Id, message = d.GetMessage() }).ToList() };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new { success = false, stage = "run", error = $"Script did not finish within {timeout.TotalSeconds:0}s.", output = Trim(output), elapsedMs = stopwatch.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new
            {
                success = false,
                stage = "run",
                error = $"{ex.GetType().FullName}: {ex.Message}",
                stackTrace = ex.StackTrace?.Split('\n').Take(8).Select(l => l.Trim()).ToList(),
                output = Trim(output),
                elapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }
        finally
        {
            Console.SetOut(previous);
            ConsoleGate.Release();
        }
    }

    private static string Trim(StringBuilder output)
    {
        const int max = 64 * 1024;
        var text = output.ToString();
        return text.Length <= max ? text : text[..max] + $"\n… ({text.Length - max} more characters)";
    }

    private static object? Render(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool or int or long or double or float or decimal or short or byte:
                return value;
            case Exception ex:
                return $"{ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(value, ToolJson.Options);
            return json.Length > 16 * 1024 ? json[..(16 * 1024)] + "…" : System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return value.ToString();
        }
    }
}
