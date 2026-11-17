# Phase 1 — Project Foundation (Historical .NET Implementation)

> **Historical record:** This document describes the original .NET Foundation delivered before the owner approved a complete migration to NestJS. It is retained as historical evidence only. It is not the target architecture and its implementation will be replaced during the NestJS migration. See [NESTJS-MIGRATION-PLAN.md](NESTJS-MIGRATION-PLAN.md).

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
- Dockerfile, PostgreSQL Compose service, local environment template and CI workflow.
- Module assembly policy: create each module project when its implementation phase starts instead of committing 18 empty placeholder assemblies; see [module boundaries](../src/Modules/README.md).

## Persistence boundary

The API host owns connection-pool composition and infrastructure health only. It does not own a shared commerce DbContext.

Each business module owns its DbContext, EF configurations, schema and migrations. Since no business module has been implemented in Phase 1, creating an empty central DbContext or a fake initial migration would violate the approved module ownership boundary and create misleading migration history. EF Core and provider versions are pinned centrally; the first actual DbContext, design-time factory and migration will be added with the first module that owns persisted tables. Migration application must remain a controlled deployment step, not automatic on every API replica startup.

## Deliberately not implemented yet

- Module-owned DbContext, design-time factory and migrations (introduced with the first persistence-owning module).
- Module business logic, authentication, catalog, inventory, checkout or payments.
- Redis, message broker, distributed job framework or generic repository layer.

## Historical version alignment

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

## Verification

GitHub Actions run [#54](https://github.com/hamedeslami/Enterprise-E-Commerce-Platform/actions/runs/43584199385) completed successfully for commit `7e7d837882abbec650b50f5f41f79551bb78a11d`.

- NuGet restore: passed.
- Release build: passed.
- Unit and integration tests: passed, including PostgreSQL-backed readiness.
- Docker Compose image build and service startup: passed.
- API liveness, PostgreSQL readiness (`SELECT 1`) and Development OpenAPI smoke checks: passed.
- Compose teardown: passed.

This verifies the current foundation and its CI Compose path. It does not constitute a production security review or production deployment certification.

## Remaining work

- Add the first module-owned DbContext, design-time factory and migration with the first persistence-owning module.
- Keep migrations as a controlled deployment operation.
- Complete security, business logic, and module-specific tests in their respective phases.
