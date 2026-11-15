using Ecommerce.Modules.Identity.Domain;
using Ecommerce.Modules.Identity.Persistence;
using Ecommerce.Modules.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecommerce.Modules.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDataProtection();

        services.AddDbContext<IdentityDbContext>((serviceProvider, options) =>
        {
            var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();

            options.UseNpgsql(
                dataSource,
                postgres => postgres.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    IdentityDbContext.Schema));
        });

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.User.RequireUniqueEmail = false;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<IdentityDbContext>()
            .AddDefaultTokenProviders();

        services.AddScoped<RefreshTokenRotationService>();

        return services;
    }
}
