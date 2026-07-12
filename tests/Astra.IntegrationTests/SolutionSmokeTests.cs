using Astra.Contracts;

namespace Astra.IntegrationTests;

public sealed class SolutionSmokeTests
{
    [Fact]
    public void ContractsAssembly_IsLoadable()
    {
        Assert.Equal("Astra.Contracts", typeof(IPlayerAccountGrain).Assembly.GetName().Name);
    }
}
