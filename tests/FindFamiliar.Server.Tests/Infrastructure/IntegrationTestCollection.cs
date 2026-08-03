namespace FindFamiliar.Server.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationTestCollection : ICollectionFixture<FindFamiliarWebApplicationFactory>
{
    public const string Name = "Find Familiar integration tests";
}
