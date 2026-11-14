using Ecommerce.Api;
using Ecommerce.Api.Health;
using Ecommerce.BuildingBlocks;
using Ecommerce.Modules.Identity;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, loggerConfiguration) =>
    loggerConfiguration
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console());

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});

builder.Services.AddOpenApi();

builder.Services.AddSingleton(serviceProvider =>
{
    var postgresConnectionString = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
        .GetConnectionString("Postgres");

    if (string.IsNullOrWhiteSpace(postgresConnectionString))
    {
        throw new InvalidOperationException(
            "The PostgreSQL connection string is missing. Configure ConnectionStrings:Postgres.");
    }

    return NpgsqlDataSource.Create(postgresConnectionString);
});

builder.Services
    .AddHealthChecks()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy(),
        tags: ["live"])
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

builder.Services.AddBuildingBlocks(builder.Configuration);
builder.Services.AddIdentityModule();
builder.Services.AddValidatorsFromAssemblyContaining<ApiAssemblyMarker>();

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.Run();

public partial class Program
{
}
