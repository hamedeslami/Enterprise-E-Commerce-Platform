using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ecommerce.Api.Health;

public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return result is int value && value == 1
                ? HealthCheckResult.Healthy("PostgreSQL is accepting queries.")
                : HealthCheckResult.Unhealthy("PostgreSQL returned an unexpected readiness result.");
        }
        catch (NpgsqlException exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.", exception);
        }
        catch (TimeoutException exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection timed out.", exception);
        }
    }
}
