using Ecommerce.Modules.Identity.Domain;
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

    private static void AssertUuidV7(Guid value)
    {
        Assert.NotEqual(Guid.Empty, value);
        Assert.Equal('7', value.ToString("N")[12]);
    }
}
