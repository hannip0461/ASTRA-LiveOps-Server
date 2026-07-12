namespace Astra.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EndToEndCollection
{
    public const string Name = "External services end-to-end";
}
