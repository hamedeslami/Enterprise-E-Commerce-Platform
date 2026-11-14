# Enterprise E-Commerce Platform

An API-first, single-vendor e-commerce backend inspired by WooCommerce, built as a .NET Modular Monolith.

## Project status

**Current phase: Phase 0 — Architecture and Technical Design**

No production application code has been started. The architecture must be reviewed and approved before Phase 1 implementation begins.

## Architecture documents

- [Architecture](docs/ARCHITECTURE.md)
- [Domain model](docs/DOMAIN-MODEL.md)
- [Database design](docs/DATABASE-DESIGN.md)
- [API design](docs/API-DESIGN.md)
- [Security](docs/SECURITY.md)
- [Roadmap](docs/ROADMAP.md)

## Target stack

- .NET 11 / ASP.NET Core 11
- C# 15
- Entity Framework Core 11
- PostgreSQL
- Redis only when justified by a measured need
- Docker Compose
- xUnit

## Scope

Single-vendor store. The platform exposes REST APIs and does not depend on a particular frontend.
