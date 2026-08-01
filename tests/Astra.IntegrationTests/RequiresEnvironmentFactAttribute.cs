namespace Astra.IntegrationTests;

/// <summary>
/// Marks a test that needs an opt-in environment variable set to <c>1</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresEnvironmentFactAttribute : FactAttribute
{
    public RequiresEnvironmentFactAttribute(string variable)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(variable), "1", StringComparison.Ordinal))
        {
            Skip = $"Set {variable}=1 to run this test.";
        }
    }
}
