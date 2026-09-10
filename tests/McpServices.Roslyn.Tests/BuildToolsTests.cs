using McpServices.Roslyn.Tools;

namespace McpServices.Roslyn.Tests;

public sealed class BuildParserTests
{
    [Fact]
    public void ParseMsBuild_extracts_file_position_code_and_project_and_dedupes()
    {
        const string output = """
            /src/Lib/Calculator.cs(12,9): error CS0103: The name 'x' does not exist in the current context [/src/Lib/Lib.csproj]
            /src/Lib/Calculator.cs(12,9): error CS0103: The name 'x' does not exist in the current context [/src/Lib/Lib.csproj]
            /src/App/Program.cs(3,1): warning CS8602: Dereference of a possibly null reference. [/src/App/App.csproj::TargetFramework=net10.0]
            error NU1101: Unable to find package Foo. No packages exist with this id in source(s): nuget.org [/src/App/App.csproj]
            Build FAILED.
            """;

        var messages = BuildTools.ParseMsBuild(output);

        Assert.Equal(3, messages.Count);
        Assert.All(messages.Take(2), m => Assert.Equal("error", m.Severity));
        var error = messages.Single(m => m.Code == "CS0103");
        Assert.Equal("error", error.Severity);
        Assert.Equal("CS0103", error.Code);
        Assert.Equal("/src/Lib/Calculator.cs", error.File);
        Assert.Equal(12, error.Line);
        Assert.Equal(9, error.Column);
        Assert.Equal("/src/Lib/Lib.csproj", error.Project);
        Assert.Contains(messages, m => m.Code == "NU1101" && m.File is null);
        Assert.Contains(messages, m => m.Severity == "warning" && m.Code == "CS8602");
    }

    [Fact]
    public void ParseTrx_reads_outcomes_and_failure_messages()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="Sample.Tests.Adds" outcome="Passed" duration="00:00:00.0123456" />
                <UnitTestResult testName="Sample.Tests.Fails" outcome="Failed" duration="00:00:01.5000000">
                  <Output>
                    <ErrorInfo>
                      <Message>Assert.Equal() Failure</Message>
                      <StackTrace>at Sample.Tests.Fails()</StackTrace>
                    </ErrorInfo>
                  </Output>
                </UnitTestResult>
                <UnitTestResult testName="Sample.Tests.Skipped" outcome="NotExecuted" duration="00:00:00" />
              </Results>
            </TestRun>
            """);

        try
        {
            var results = BuildTools.ParseTrx(path).ToList();
            Assert.Equal(3, results.Count);
            Assert.Equal("passed", results[0].Outcome);
            Assert.Equal(12, results[0].DurationMs);
            var failed = results.Single(r => r.Outcome == "failed");
            Assert.Equal("Assert.Equal() Failure", failed.Error);
            Assert.Equal(1500, failed.DurationMs);
            Assert.Contains("Sample.Tests.Fails", failed.StackTrace, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CyclomaticComplexity_counts_branches()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            class C
            {
                int M(int x, bool a, bool b)
                {
                    if (x > 0 && a) return 1;
                    for (var i = 0; i < x; i++) { if (b || a) x--; }
                    return x switch { 1 => 1, 2 => 2, _ => 0 };
                }
            }
            """);
        var body = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single().Body!;

        // 1 + if + && + for + if + || + 3 switch arms = 9
        Assert.Equal(9, DiagnosticsTools.CyclomaticComplexity(body));
        Assert.Equal(2, DiagnosticsTools.MaxNesting(body));
    }
}

[Collection(SampleSolutionTests.Name)]
public sealed class BuildToolTests(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Build_project_builds_the_sample_solution()
    {
        var result = await fixture.Server.CallJsonAsync("build_project", new { path = fixture.SolutionPath, timeoutSeconds = 600 });
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(0, result.GetProperty("errors").GetInt32());
        Assert.False(result.GetProperty("timedOut").GetBoolean());
    }
}
