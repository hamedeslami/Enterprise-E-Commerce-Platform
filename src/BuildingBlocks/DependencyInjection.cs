using Ecommerce.BuildingBlocks.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.BuildingBlocks;

public static class DependencyInjection
{
    public static IServiceCollection AddBuildingBlocks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<StoreOptions>()
            .Bind(configuration.GetSection(StoreOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => TimeZoneInfo.TryFindSystemTimeZoneById(options.TimeZone, out _),
                "Store:TimeZone must be a valid IANA or system time-zone identifier.")
            .Validate(
                options => IsSupportedCulture(options.Locale),
                "Store:Locale must be a supported .NET culture name.")
            .ValidateOnStart();

        return services;
    }

    private static bool IsSupportedCulture(string locale)
    {
        try
        {
            _ = System.Globalization.CultureInfo.GetCultureInfo(locale);
            return true;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return false;
        }
    }
}
