using Ecommerce.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecommerce.IntegrationTests.Identity;

[Collection("Identity database")]
public sealed class IdentityPersistenceTests : IClassFixture<ApiFactory>
{
    private const string InitialMigrationId = "20261114104352_InitialIdentitySchema";

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
        Assert.Equal(ValueGenerated.Never, userId!.ValueGenerated);
    }

    [Fact]
    public async Task MigrateAsync_CreatesIdentitySchemaAndTables()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        await dbContext.Database.MigrateAsync();

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync();
        Assert.Contains(InitialMigrationId, appliedMigrations);

        var usersTableExists = await dbContext.Database
            .SqlQuery<bool>($"SELECT to_regclass('identity.\"AspNetUsers\"') IS NOT NULL AS \"Value\"")
            .SingleAsync();

        Assert.True(usersTableExists);
    }
}
