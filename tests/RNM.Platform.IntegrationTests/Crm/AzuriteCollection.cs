using Xunit;

namespace RNM.Platform.IntegrationTests.Crm;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteTableFixture>
{
    public const string Name = "Azurite";
}
