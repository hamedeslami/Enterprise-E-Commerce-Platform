using Ecommerce.Modules.Identity.Domain;
using Ecommerce.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecommerce.IntegrationTests.Identity;

public sealed class IdentityAccountTypeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public IdentityAccountTypeTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task SaveChangesAsync_RejectsUnspecifiedAccountType()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db.Users.Add(new ApplicationUser { UserName = $"invalid-{Guid.NewGuid():N}" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(AccountType.Customer)]
    [InlineData(AccountType.Staff)]
    public async Task SaveChangesAsync_PersistsExplicitAccountType(AccountType accountType)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await db.Database.MigrateAsync();
        var user = new ApplicationUser
        {
            UserName = $"account-{Guid.NewGuid():N}",
            AccountType = accountType
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var persisted = await db.Users.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id);
        Assert.Equal(accountType, persisted.AccountType);
    }
}
