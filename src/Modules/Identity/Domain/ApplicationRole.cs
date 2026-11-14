using Microsoft.AspNetCore.Identity;

namespace Ecommerce.Modules.Identity.Domain;

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
        Id = Guid.CreateVersion7();
    }
}
