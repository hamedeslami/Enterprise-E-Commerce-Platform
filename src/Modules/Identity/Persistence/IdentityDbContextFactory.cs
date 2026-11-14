using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecommerce.Modules.Identity.Persistence;

public sealed class IdentityDbContextFactory
    : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Database=ecommerce;Username=ecommerce;Password=design-time-only";

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    IdentityDbContext.Schema))
            .Options;

        return new IdentityDbContext(options);
    }
}
