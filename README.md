# Enterprise E-Commerce Platform

An API-first, single-vendor e-commerce backend inspired by WooCommerce, built as a .NET Modular Monolith.

## Project status

**Current phase: Phase 1 — Project Foundation (in progress).**

Phase 0 architecture and technical design were explicitly approved on 2026-11-14. Implementation is taking place on the `phase-1/project-foundation` branch. No commerce business modules have been implemented yet.

## Architecture documents

- [Architecture](docs/ARCHITECTURE.md)
- [Domain model](docs/DOMAIN-MODEL.md)
- [Database design](docs/DATABASE-DESIGN.md)
- [API design](docs/API-DESIGN.md)
- [Security](docs/SECURITY.md)
- [Roadmap](docs/ROADMAP.md)
- [Phase 1 foundation notes](docs/PHASE-1-FOUNDATION.md)

## Target stack

- .NET 10 LTS / ASP.NET Core 10 / C# 14
- Entity Framework Core 10 (module-owned contexts)
- Npgsql EF Core provider 10.0.3
- PostgreSQL 18
- Redis only when justified by a measured need
- Docker Compose
- xUnit

## Scope

Single-vendor store targeting Iran initially. Store prices and canonical commerce amounts are expressed in toman. The platform exposes REST APIs and does not depend on a particular frontend.

**Currency integration note:** toman is the store's business unit. Do not assume it is an ISO 4217 currency code. Any rial/toman conversion required by a payment provider must happen explicitly in that provider adapter, according to the provider's documented contract.

## Local development

Prerequisites:
- .NET SDK 10.0.404 or a compatible later 10.0 feature band
- Docker Engine with Docker Compose plugin

Start PostgreSQL and the API:

```bash
cp .env.example .env
# Change the local development password in .env before starting.
docker compose up --build
```

The API listens on `http://localhost:8080`. Liveness is available at `/health/live`; readiness at `/health/ready` verifies a real PostgreSQL connection and executes `SELECT 1`. The OpenAPI document is available at `/openapi/v1.json` in Development.

To run tests locally, start PostgreSQL first. The integration-test default connection string matches the checked-in `.env.example`; if you change its password, set `TEST_POSTGRES_CONNECTION_STRING` to the matching test database connection string before running tests. For example, on PowerShell:

```powershell
$env:TEST_POSTGRES_CONNECTION_STRING = "Host=127.0.0.1;Port=5432;Database=ecommerce;Username=ecommerce;Password=YOUR_LOCAL_PASSWORD"
dotnet test Ecommerce.sln
```

Build and test commands:

```bash
dotnet restore Ecommerce.sln
dotnet build Ecommerce.sln --configuration Release --no-restore
dotnet test Ecommerce.sln --configuration Release --no-build
```

The EF Core 10 and Npgsql 10 provider versions are pinned centrally. Each business module will own its DbContext and migrations. The API host intentionally does not contain a shared commerce DbContext; the first real context and migration will be introduced with the module that owns its tables.
