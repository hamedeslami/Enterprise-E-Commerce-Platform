using Ecommerce.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecommerce.IntegrationTests.Identity;

public sealed class IdentityPersistenceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public IdentityPersistenceTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void IdentityDbContext_IsRegisteredWithDedicatedSchema()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        Assert.Equal(IdentityDbContext.Schema, dbContext.Model.GetDefaultSchema());

        var userId = dbContext.Model
            .FindEntityType(typeof(Ecommerce.Modules.Identity.Domain.ApplicationUser))!
            .FindProperty(nameof(Ecommerce.Modules.Identity.Domain.ApplicationUser.Id));

        Assert.NotNull(userId);
        Assert.Value(false, userId!.ValueGenerated);
    }
}
