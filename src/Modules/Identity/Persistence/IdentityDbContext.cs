using Ecommerce.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Modules.Identity.Persistence;

public sealed class IdentityDbContext(
    DbContextOptions<IdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public const string Schema = "identity";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>()
            .Property(user => user.Id)
            .ValueGeneratedNever();

        builder.Entity<ApplicationRole>()
            .Property(role => role.Id)
            .ValueGeneratedNever();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateUuidV7Identifiers();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidateUuidV7Identifiers();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateUuidV7Identifiers()
    {
        var invalidUser = ChangeTracker
            .Entries<ApplicationUser>()
            .Any(entry => entry.State == EntityState.Added && !IsUuidV7(entry.Entity.Id));

        var invalidRole = ChangeTracker
            .Entries<ApplicationRole>()
            .Any(entry => entry.State == EntityState.Added && !IsUuidV7(entry.Entity.Id));

        if (invalidUser || invalidRole)
        {
            throw new InvalidOperationException(
                "New Identity users and roles must use application-generated UUIDv7 identifiers.");
        }
    }

    private static bool IsUuidV7(Guid value) =>
        value != Guid.Empty && value.ToString("N")[12] == '7';
}
