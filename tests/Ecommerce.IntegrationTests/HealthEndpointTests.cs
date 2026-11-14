using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecommerce.IntegrationTests;

public sealed class HealthEndpointTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LivenessEndpoint_ReturnsHealthy_WithoutCheckingDatabase()
    {
        using var response = await _client.GetAsync("/health/live");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadinessEndpoint_ExecutesQueryAgainstPostgres()
    {
        using var response = await _client.GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApiEndpoint_IsAvailableInDevelopmentHost()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("openapi", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
