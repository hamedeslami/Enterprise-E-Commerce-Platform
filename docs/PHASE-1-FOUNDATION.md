# Phase 1 — Project Foundation

**Status:** In progress.

## Implemented foundation

- .NET 10 SDK pin (10.0.404) and shared build properties targeting `net10.0` / C# 14.
- Central Package Management for stable packages used by the API and tests; EF Core 10.0.15 and Npgsql EF Core provider 10.0.3 are pinned for module-owned persistence.
- ASP.NET Core 10 API host.
- Global Problem Details registration with trace identifier.
- Built-in OpenAPI document endpoint.
- Separate liveness and database readiness health checks.
- PostgreSQL connection pool registered through a singleton `NpgsqlDataSource`.
- Readiness check opens a real PostgreSQL connection and executes `SELECT 1`.
- Serilog host integration with console sink.
- FluentValidation assembly scanning.
- Validated store locale and timezone options; defaults are `fa-IR` and `Asia/Tehran`.
- Unit and API integration test projects; integration tests exercise PostgreSQL readiness.
- Dockerfile, PostgreSQL Compose service, local environment template and CI workflow with a PostgreSQL 18 service.
- Module assembly policy: create each module project when its implementation phase starts instead of committing 18 empty placeholder assemblies; see [module boundaries](../src/Modules/README.md).

## Persistence boundary

The API host owns connection-pool composition and infrastructure health only. It does not own a shared commerce DbContext.

Each business module owns its DbContext, EF configurations, schema and migrations. Since no business module has been implemented in Phase 1, creating an empty central DbContext or a fake initial migration would violate the approved module ownership boundary and create misleading migration history. EF Core and provider versions are pinned centrally; the first actual DbContext, design-time factory and migration will be added with the first module that owns persisted tables. Migration application must remain a controlled deployment step, not automatic on every API replica startup.

## Deliberately not implemented yet

- Module-owned DbContext, design-time factory and migrations (introduced with the first persistence-owning module).
- Module business logic, authentication, catalog, inventory, checkout or payments.
- Redis, message broker, distributed job framework or generic repository layer.

## Version alignment

The owner approved moving from .NET 11 to .NET 10 LTS on 2026-11-14. The selected stable baseline is:

| Component | Version |
|---|---:|
| .NET SDK | 10.0.404 |
| .NET / ASP.NET Core | 10.0.15 runtime line |
| C# | 14 |
| EF Core | 10.0.15 |
| Npgsql EF Core provider | 10.0.3 |
| Npgsql ADO.NET driver | 10.0.3 |
| PostgreSQL | 18 |

Npgsql EF Core provider 10.0.3 targets .NET 10 and declares dependencies on EF Core / EF Core Relational >= 10.0.4 and < 11.0.0, plus Npgsql >= 10.0.3. This is compatible with the centrally pinned EF Core 10.0.15 and Npgsql 10.0.3 versions. No prerelease package is required.

## Verification still required

- Restore and Release build on .NET SDK 10.0.404.
- Unit and integration tests, including the real PostgreSQL readiness query.
- Docker Compose startup and PostgreSQL health check.
- API liveness, readiness and OpenAPI smoke tests.
- Module-owned migration generation/application once the first persistence-owning module is introduced.

The current execution environment does not have the .NET SDK or Docker CLI installed. GitHub Actions is configured to run against PostgreSQL 18; until a successful run is observed, the solution is not considered build-verified.
