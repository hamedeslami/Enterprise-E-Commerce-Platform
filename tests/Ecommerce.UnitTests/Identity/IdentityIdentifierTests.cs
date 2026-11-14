using Ecommerce.Modules.Identity.Domain;
using Ecommerce.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecommerce.UnitTests.Identity;

public sealed class IdentityIdentifierTests
{
    [Fact]
    public void NewApplicationUser_UsesUuidVersion7()
    {
        var user = new ApplicationUser();

        AssertUuidV7(user.Id);
    }

    [Fact]
    public void NewApplicationRole_UsesUuidVersion7()
    {
        var role = new ApplicationRole();

        AssertUuidV7(role.Id);
    }

    [Fact]
    public void Context_RejectsNonUuidV7UserIdentifier_OnSaveChanges()
    {
        using var context = CreateContext();
        context.Users.Add(new ApplicationUser
        {
            Id = Guid.Parse("8f4f0d40-4f2b-4a14-a3b7-5c1d1a3f1001")
        });

        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    [Fact]
    public void Context_RejectsNonUuidV7RoleIdentifier_OnSaveChanges()
    {
        using var context = CreateContext();
        context.Roles.Add(new ApplicationRole
        {
            Id = Guid.Parse("8f4f0d40-4f2b-4a14-a3b7-5c1d1a3f1001")
        });

        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    [Fact]
    public async Task Context_RejectsNonUuidV7UserIdentifier_OnSaveChangesAsync()
    {
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser
        {
            Id = Guid.Parse("8f4f0d40-4f2b-4a14-a3b7-5c1d1a3f1001")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Context_RejectsNonUuidV7RoleIdentifier_OnSaveChangesAsync()
    {
        await using var context = CreateContext();
        context.Roles.Add(new ApplicationRole
        {
            Id = Guid.Parse("8f4f0d40-4f2b-4a14-a3b7-5c1d1a3f1001")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync());
    }

    private static IdentityDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=ecommerce;Username=ecommerce;Password=not-used")
            .Options;

        return new IdentityDbContext(options);
    }

    private static void AssertUuidV7(Guid value)
    {
        Assert.NotEqual(Guid.Empty, value);

        var formatted = value.ToString("N");
        Assert.Equal('7', formatted[12]);
        Assert.Contains(formatted[16], new[] { '8', '9', 'a', 'b' });
    }
}
