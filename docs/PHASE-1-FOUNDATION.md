# Phase 1 — Project Foundation

**Status:** In progress.

## Implemented foundation

- .NET 10 SDK pin (10.0.404) and shared build properties targeting `net10.0` / C# 14.
- Central Package Management for stable packages used by the API and tests, including EF Core 10.0.15 and Npgsql EF Core provider 10.0.3.
- ASP.NET Core 10 API host.
- Global Problem Details registration with trace identifier.
- Built-in OpenAPI document endpoint.
- Liveness health check.
- Serilog host integration with console sink.
- FluentValidation assembly scanning.
- Validated store locale and timezone options; defaults are `fa-IR` and `Asia/Tehran`.
- Unit and API integration test projects.
- Dockerfile, PostgreSQL Compose service, local environment template and CI workflow.
- Module assembly policy: create each module project when its implementation phase starts instead of committing 18 empty placeholder assemblies; see [module boundaries](../src/Modules/README.md).

## Deliberately not implemented yet

- EF Core DbContext/provider wiring and migrations.
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
| PostgreSQL | 18 |

The Npgsql EF Core provider 10.0.3 declares dependencies on EF Core and EF Core Relational >= 10.0.4 and < 11.0.0, so it is aligned with EF Core 10.0.15. No prerelease package is required.

## Verification still required

- Restore, Release build and unit/integration tests on .NET SDK 10.0.404.
- Docker Compose startup and PostgreSQL health check.
- API liveness and OpenAPI smoke tests.
- PostgreSQL connection and migration smoke test after persistence wiring.

The current execution environment does not have the .NET SDK or Docker CLI installed. The GitHub Actions workflow is the remote build/test verifier; until a successful run is observed, the solution is not considered build-verified.
