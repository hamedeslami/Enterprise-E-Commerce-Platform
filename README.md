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

- .NET 11 / ASP.NET Core 11 / C# 15
- Entity Framework Core 11 (stable package)
- PostgreSQL
- Redis only when justified by a measured need
- Docker Compose
- xUnit

## Scope

Single-vendor store targeting Iran initially. Store prices and canonical commerce amounts are expressed in toman. The platform exposes REST APIs and does not depend on a particular frontend.

**Currency integration note:** toman is the store's business unit. Do not assume it is an ISO 4217 currency code. Any rial/toman conversion required by a payment provider must happen explicitly in that provider adapter, according to the provider's documented contract.

## Local development

Prerequisites:
- .NET SDK 11.0.100 or a compatible later 11.0 feature band
- Docker Engine with Docker Compose plugin

Start PostgreSQL and the API:

```bash
cp .env.example .env
# Change the local development password in .env before starting.
docker compose up --build
```

The API listens on `http://localhost:8080`; liveness is available at `/health/live` and the OpenAPI document at `/openapi/v1.json`.

To build and test without Docker:

```bash
dotnet restore Ecommerce.sln
dotnet build Ecommerce.sln --no-restore
dotnet test Ecommerce.sln --no-build
```

The database provider and EF Core migration wiring are deliberately not declared complete until a stable Npgsql provider compatible with EF Core 11 is available. See [Phase 1 foundation notes](docs/PHASE-1-FOUNDATION.md).