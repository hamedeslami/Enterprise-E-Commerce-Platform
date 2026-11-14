using Ecommerce.BuildingBlocks;
using Ecommerce.BuildingBlocks.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecommerce.UnitTests;

public sealed class StoreOptionsTests
{
    [Fact]
    public void Defaults_AreIranianStoreLocaleAndTimeZone()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddBuildingBlocks(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<StoreOptions>>().Value;

        Assert.Equal("fa-IR", options.Locale);
        Assert.Equal("Asia/Tehran", options.TimeZone);
    }

    [Fact]
    public void InvalidTimeZone_IsRejectedOnOptionsAccess()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:TimeZone"] = "Not/A_Real_Time_Zone"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddBuildingBlocks(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<StoreOptions>>().Value);
    }
}
