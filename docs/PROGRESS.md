# Project Progress

**Current phase:** Phase 0 — Architecture and Technical Design  
**Status:** Draft documents committed; awaiting owner review and approval.  
**Implementation status:** No application code, migrations or runtime infrastructure have been created.

## Repository

- Repository: https://github.com/hamedeslami/Enterprise-E-Commerce-Platform
- Working branch: docs/phase-0-architecture
- Base branch: main

## Decisions confirmed

- Store model: Single-Vendor.
- Primary database: PostgreSQL.
- Environments: local development and Docker Compose.
- API-first backend; frontend is a separate future client.
- Modular Monolith; not microservices.
- Authentication target: JWT access + refresh tokens in separate HttpOnly cookies with CSRF defenses.
- Initial target country/market: Iran.
- Store price and commerce amount unit: whole-integer toman amounts (`long` / PostgreSQL `BIGINT` / JSON integer).

## Phase 0 documents

- [Architecture](ARCHITECTURE.md)
- [Domain Model](DOMAIN-MODEL.md)
- [Database Design](DATABASE-DESIGN.md)
- [API Design](API-DESIGN.md)
- [Security](SECURITY.md)
- [Roadmap](ROADMAP.md)

## Pending owner decisions

- Rounding policy for percentage/rate calculations that produce fractional toman results.
- Applicable Iranian tax rules and tax-inclusive/exclusive pricing policy.
- Guest checkout requirement.
- Store timezone.
- Frontend/API origin topology (same-site or cross-site).
- Initial payment, shipping, email/SMS and media storage providers.
- SKU uniqueness scope and whether fractional inventory is required.
- Stock reservation TTL is confirmed: default 15 minutes, configurable at store level. Expired unpaid reservations must be released idempotently. Backorder policy is confirmed: configurable per product/variant, default disabled (`NoBackorders`); supported modes: `NoBackorders`, `AllowBackorders`, `AllowBackordersNotify`.
- Identifier strategy (UUID/ULID or bigint).

The whole-toman amount representation is confirmed; the precise rounding rule for percentage/rate calculations must be finalized before pricing/tax implementation. These decisions can be finalized in the relevant phase if they do not block foundation design. No assumptions should silently become implemented business rules.

## Next step

Review the six Phase 0 design documents. Record requested changes and approve the architecture. Phase 1 must not begin until approval is explicit.
