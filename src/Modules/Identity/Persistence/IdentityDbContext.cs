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

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(entity => entity.Id).ValueGeneratedNever();
            user.Property(entity => entity.AccountType).IsRequired();
            user.ToTable(table => table.HasCheckConstraint(
                "CK_AspNetUsers_AccountType",
                "\"AccountType\" IN (1, 2)"));
        });

        builder.Entity<ApplicationRole>()
            .Property(role => role.Id)
            .ValueGeneratedNever();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateAddedIdentityEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidateAddedIdentityEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateAddedIdentityEntities()
    {
        var invalidUser = ChangeTracker
            .Entries<ApplicationUser>()
            .Any(entry => entry.State == EntityState.Added &&
                (!IsUuidV7(entry.Entity.Id) ||
                 entry.Entity.AccountType is not (AccountType.Customer or AccountType.Staff)));

        var invalidRole = ChangeTracker
            .Entries<ApplicationRole>()
            .Any(entry => entry.State == EntityState.Added && !IsUuidV7(entry.Entity.Id));

        if (invalidUser || invalidRole)
        {
            throw new InvalidOperationException(
                "New Identity users require an application-generated UUIDv7 and an explicit Customer or Staff account type; roles require UUIDv7 identifiers.");
        }
    }

    private static bool IsUuidV7(Guid value)
    {
        if (value == Guid.Empty) return false;
        var formatted = value.ToString("N");
        return formatted[12] == '7' && formatted[16] is '8' or '9' or 'a' or 'b';
    }
}
