using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ecommerce.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var connectionString =
                Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION_STRING")
                ?? "Host=127.0.0.1;Port=5432;Database=ecommerce;Username=ecommerce;Password=change-this-local-password;Timeout=5;Command Timeout=5";

            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString
            });
        });
    }
}
