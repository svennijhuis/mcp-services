using Xunit;

namespace McpServices.TestSupport;

/// <summary>
/// A fact that only runs when an environment variable is set, used for tests that need external
/// infrastructure (PostgreSQL, SQL Server, Docker). Skipped tests show the variable to set.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string environmentVariable)
    {
        EnvironmentVariable = environmentVariable;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = $"Set {environmentVariable} to run this test.";
        }
    }

    public string EnvironmentVariable { get; }
}
