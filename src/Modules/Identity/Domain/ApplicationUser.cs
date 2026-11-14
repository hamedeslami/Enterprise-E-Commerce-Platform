using Microsoft.AspNetCore.Identity;

namespace Ecommerce.Modules.Identity.Domain;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        Id = Guid.CreateVersion7();
    }
}
