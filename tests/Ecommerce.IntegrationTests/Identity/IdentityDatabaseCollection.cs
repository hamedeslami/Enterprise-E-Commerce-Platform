using Xunit;

namespace Ecommerce.IntegrationTests.Identity;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdentityDatabaseCollection
{
    public const string Name = "Identity database";
}
