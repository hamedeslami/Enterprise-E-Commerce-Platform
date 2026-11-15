using Ecommerce.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Modules.Identity.Persistence;

public sealed class IdentityDbContext(
    DbContextOptions<IdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public const string Schema = "identity";

    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

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

        builder.Entity<AuthSession>(session =>
        {
            session.ToTable("AuthSessions", table =>
            {
                table.HasCheckConstraint("CK_AuthSessions_ActorKind", "\"ActorKind\" IN (1, 2)");
                table.HasCheckConstraint("CK_AuthSessions_AuthenticationMethod", "\"AuthenticationMethod\" IN (1, 2, 3, 4)");
                table.HasCheckConstraint(
                    "CK_AuthSessions_Lifetime",
                    "\"CreatedAt\" < \"AbsoluteExpiresAt\" AND \"CreatedAt\" < \"IdleExpiresAt\" AND \"IdleExpiresAt\" <= \"AbsoluteExpiresAt\"");
                table.HasCheckConstraint(
                    "CK_AuthSessions_UserAgentHash",
                    "\"UserAgentHash\" IS NULL OR octet_length(\"UserAgentHash\") = 32");
            });

            session.HasKey(entity => entity.Id);
            session.Property(entity => entity.Id).ValueGeneratedNever();
            session.Property(entity => entity.RevocationReason).HasMaxLength(64);
            session.Property(entity => entity.DeviceLabel).HasMaxLength(128);
            session.Property(entity => entity.UserAgentHash).HasColumnType("bytea");

            session.HasIndex(entity => new { entity.UserId, entity.CreatedAt })
                .HasDatabaseName("IX_AuthSessions_UserId_CreatedAt");

            session.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(entity => entity.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RefreshToken>(token =>
        {
            token.ToTable("RefreshTokens", table =>
            {
                table.HasCheckConstraint(
                    "CK_RefreshTokens_HashLength",
                    "octet_length(\"SecretHash\") = 32");
                table.HasCheckConstraint(
                    "CK_RefreshTokens_Lifetime",
                    "\"CreatedAt\" < \"ExpiresAt\"");
                table.HasCheckConstraint(
                    "CK_RefreshTokens_Replacement",
                    "(\"ConsumedAt\" IS NULL AND \"ReplacedByTokenId\" IS NULL) OR (\"ConsumedAt\" IS NOT NULL AND \"ReplacedByTokenId\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_RefreshTokens_NotSelfReplacing",
                    "\"ReplacedByTokenId\" IS NULL OR \"ReplacedByTokenId\" <> \"Id\"");
            });

            token.HasKey(entity => entity.Id);
            token.Property(entity => entity.Id).ValueGeneratedNever();
            token.Property(entity => entity.SecretHash).HasColumnType("bytea").IsRequired();

            token.HasIndex(entity => entity.SecretHash)
                .IsUnique()
                .HasDatabaseName("IX_RefreshTokens_SecretHash");

            token.HasIndex(entity => entity.SessionId)
                .HasDatabaseName("IX_RefreshTokens_SessionId");

            token.HasIndex(entity => entity.SessionId)
                .IsUnique()
                .HasDatabaseName("UX_RefreshTokens_OneActivePerSession")
                .HasFilter("\"ConsumedAt\" IS NULL AND \"RevokedAt\" IS NULL");

            token.HasOne(entity => entity.Session)
                .WithMany()
                .HasForeignKey(entity => entity.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
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

        var invalidSession = ChangeTracker
            .Entries<AuthSession>()
            .Any(entry => entry.State == EntityState.Added && !IsUuidV7(entry.Entity.Id));

        var invalidRefreshToken = ChangeTracker
            .Entries<RefreshToken>()
            .Any(entry => entry.State == EntityState.Added && !IsUuidV7(entry.Entity.Id));

        if (invalidUser || invalidRole || invalidSession || invalidRefreshToken)
        {
            throw new InvalidOperationException(
                "New Identity users require an explicit account type; all Identity users, roles, sessions and refresh tokens require application-generated UUIDv7 identifiers.");
        }
    }

    private static bool IsUuidV7(Guid value)
    {
        if (value == Guid.Empty) return false;
        var formatted = value.ToString("N");
        return formatted[12] == '7' && formatted[16] is '8' or '9' or 'a' or 'b';
    }
}
