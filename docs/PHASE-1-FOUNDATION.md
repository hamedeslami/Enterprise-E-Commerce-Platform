# Phase 1 — Project Foundation

**Status:** In progress.

## Implemented foundation

- .NET 11 SDK pin and shared build properties.
- Central Package Management for stable packages used by the API and tests.
- ASP.NET Core API host.
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

- EF Core DbContext/provider/migrations.
- Module business logic, authentication, catalog, inventory, checkout or payments.
- Redis, message broker, distributed job framework or generic repository layer.

## PostgreSQL provider compatibility decision

As of 2026-11-14, EF Core 11.0.0 is stable. The Npgsql EF Core 11 provider is still at `11.0.0-rc.3`; the latest stable provider line is 10.0.3 and belongs to the EF Core 10 major line.

Do not reference Npgsql EF Core 10 from EF Core 11, and do not silently introduce a release-candidate dependency into a project whose dependency policy is stable-only.

Before database wiring, choose one of these paths:

1. **Stable-only:** use EF Core 10.0.15 with Npgsql EF Core 10.0.3 on the .NET 11 host, after a compatibility smoke test. This temporarily departs from the EF Core 11 target.
2. **Stay on EF Core 11:** use Npgsql EF Core 11.0.0-rc.3 temporarily, isolated behind Infrastructure, with explicit acceptance of prerelease risk and a tracked upgrade to stable when released.

No EF migrations should be generated until this decision is made.

## Verification still required

- Restore, Release build and unit/integration tests on .NET SDK 11.0.100.
- Docker Compose startup and PostgreSQL health check.
- API liveness and OpenAPI smoke tests.
- PostgreSQL connection and migration smoke test after the provider decision.

The current execution environment does not have the .NET SDK or Docker CLI installed. The GitHub Actions workflow is the intended remote build/test verifier; until a successful run is observed, the solution is not considered build-verified.
