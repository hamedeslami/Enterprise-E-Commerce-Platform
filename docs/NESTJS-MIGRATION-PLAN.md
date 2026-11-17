# NestJS Migration Plan

**Status:** Owner-approved migration direction. M1 (architecture and documentation) is in progress. This document does not authorize runtime-code changes.

## Owner decisions

- Replace ASP.NET Core/.NET completely with NestJS + TypeScript.
- Do not keep a hybrid or two production API hosts.
- Existing PostgreSQL data is development/test data only and may be discarded; no business data migration is required.
- Recreate the target schema through reviewed Prisma migrations. Do not port EF Core migration history.
- Preserve approved product, domain, API, security and data-integrity contracts unless an explicit decision record changes them.

## Target stack

| Concern | Selection |
|---|---|
| Runtime/framework | Supported Node.js LTS + NestJS |
| Language | TypeScript |
| Database | PostgreSQL 18 |
| ORM/migrations | Prisma Client + Prisma Migrate |
| Runtime validation | Zod through a small NestJS Pipe |
| Logging | Pino with tested redaction |
| Tests | Jest + Supertest |
| Integration database | Real PostgreSQL |
| API | REST/JSON + OpenAPI |
| Local infrastructure | Docker Compose |
| CI | GitHub Actions |
| Cache/broker | None initially; Redis only after measured need |

Exact compatible package versions are selected and pinned during M2. Commit the lockfile and use reproducible installs in CI; do not guess package versions in architecture documents.

## Migration principles

1. Preserve approved contracts and invariants, not C# classes or EF implementation details.
2. NestJS becomes the sole backend runtime; no permanent dual-stack period.
3. Development/test PostgreSQL schemas may be dropped and recreated after a disposable backup; no data-copy pipeline is needed.
4. Create a fresh Prisma schema and migration history; EF migrations remain historical evidence only.
5. PostgreSQL is authoritative. Verify constraints, transactions, row locks and concurrency against PostgreSQL itself.
6. Avoid premature Redis, message brokers, microservices, generic repositories and job frameworks.
7. Preserve security parity: strict refresh replay, Customer/Staff separation, mandatory Staff MFA, cookies/CSRF/Origin, quotas, audit and secret handling.
8. Each stage is a separate reviewable PR. No direct main changes or merge without explicit owner approval.

## Stages and exit criteria

### M1 — Architecture and migration documentation (current)

Align architecture, roadmap, progress, technical debt, security, session/token, Identity, database and API documents with NestJS. Mark framework-specific .NET documents as historical. Record implementation gates.

**Exit:** active design documents consistently describe NestJS/TypeScript/Prisma; no runtime source, database schema or CI workflow is changed.

### M2 — NestJS Foundation

Create the NestJS application; validated configuration; Prisma/PostgreSQL lifecycle; Pino with secret/PII redaction; correlation IDs; safe error/Problem Details mapping; Zod validation Pipe; OpenAPI; liveness/readiness; Docker Compose; GitHub Actions; unit/API tests and PostgreSQL smoke tests.

**Exit:** clean reproducible install, lint/format/typecheck/build/tests pass in CI; Compose starts API and PostgreSQL; liveness and real DB readiness pass; no .NET build is required for the new Foundation.

### M3 — Prisma schema and Identity persistence

Define Prisma models and PostgreSQL schema for users, roles, account type, activation state, sessions and refresh tokens. Recreate UUIDv7 handling, constraints, indexes and partial unique indexes. Create migrations from an empty development/test database. Port behavior and tests, not EF classes or migrations.

**Exit:** empty database can be built solely from Prisma migrations; constraints/indexes/nullability match approved contracts; PostgreSQL integration tests pass. No authentication endpoint is implied.

### M4 — Atomic refresh rotation and security regression

Reimplement rotation with PostgreSQL transactions and explicit SQL as required. Enforce the approved lock order: owning user → pending authentication transaction (where applicable) → session rows in stable UUID order → refresh-token rows. Preserve constant-time secret verification, database-time expiry, conditional consumption, successor insertion and strict replay revocation.

**Exit:** success, wrong-secret behavior, strict replay, concurrent same-token refresh, expiry, rollback and at-most-one-active-token tests pass against PostgreSQL. No replay grace period is introduced.

### M5 — Remove legacy .NET implementation

Remove .NET solution/projects, C# source, EF migrations/configuration, old Docker build artifacts and .NET-only CI steps. Retain only clearly labeled historical records where useful.

**Exit:** no executable/build/test dependency on .NET remains; NestJS CI and PostgreSQL smoke tests pass from a clean checkout; documentation names NestJS as the sole backend.

### M6 — Resume Identity authentication implementation

After M1–M5 are accepted, resume approved Phase 2 slices: JWT/session issuance, Customer password/OTP, Staff provisioning/MFA, cookie/CSRF/Origin/CORS, session management and security review. Do not start Catalog/Inventory until Identity is implemented and its exit criteria pass.

## Security and data invariants

- Customer and Staff remain distinct persisted account types, independent of roles.
- `IsActive` remains server-controlled and independent of temporary lockout and `MustChangePassword`.
- UUIDv7 remains the identifier strategy.
- Access JWT uses RS256, unique `kid`, public-key overlap and fail-closed production startup; maximum lifetime remains five minutes.
- Refresh credentials remain opaque, high-entropy, hash-only at rest, single-use and transactionally rotated.
- Valid consumed-token replay strictly revokes the owning session; no grace period.
- Lock order remains user → pending authentication transaction → session → refresh token.
- Staff cannot receive a normal session before mandatory MFA; temporary-password change remains mandatory where flagged.
- OTP remains short-lived, purpose-bound, single-use, HMAC-protected and quota-limited; delivery uses a PostgreSQL transactional outbox.
- Access/refresh credentials remain separate HttpOnly cookies. Unsafe cookie-authenticated requests require CSRF and exact Origin checks; CORS remains an exact allow-list.
- Secrets and encryption/signing keys are never stored in source, images or plaintext database fields.
- Critical state changes remain transactional, idempotent and covered by PostgreSQL integration/concurrency tests.

## Implementation guardrails and open gates

**Prisma concurrency:** Prisma transactions alone do not prove correctness. Use explicit transaction boundaries, conditional updates and parameterized raw SQL for PostgreSQL row locks (for example `SELECT ... FOR UPDATE`) when needed. Test lock ordering, rollback, races and deadlocks on PostgreSQL; never substitute process-local mutexes for database guarantees.

**BIGINT and JSON:** PostgreSQL `BIGINT` can exceed JavaScript's safe integer range. Use TypeScript `bigint` internally and define explicit DTO/JSON serialization and validation. Never silently coerce unsafe values to `number`. Monetary API wire representation must be documented and round-trip tested.

**Password hashing:** ASP.NET Core Identity password primitives disappear. Select and review the concrete maintained password-hashing implementation, parameters, versioned hash format and rehash-on-login policy before the first authentication endpoint (TD-MIG-001).

**CSRF:** Replace ASP.NET antiforgery middleware references with a framework-independent synchronizer-token contract integrated with NestJS; bind tokens to browser/session context and test hostile origins before exposing unsafe cookie-authenticated endpoints (TD-MIG-002).

**Encryption:** Replace framework Data Protection references with an application-owned, versioned authenticated-encryption boundary for TOTP secrets and temporary outbox payloads. Select AEAD construction, key custody and rotation before production use (TD-MIG-003). JWT signing keys are a separate cryptographic purpose.

**Logging:** Pino redaction must remove passwords, OTPs, access/refresh tokens, CSRF values, TOTP/recovery secrets, authorization headers, cookies and protected payloads. Add tests proving redaction (TD-MIG-007).

## Database reset policy

The owner confirmed there is no important PostgreSQL data. Development/test schemas may be dropped and recreated during M3. Take a disposable backup as an operational safeguard. Do not build an EF-to-Prisma data-copy path. This decision does not authorize destructive production database operations.

## Migration completion

Migration is complete only when M1–M5 are reviewed and accepted, NestJS is the only backend runtime, Prisma can build the schema from an empty PostgreSQL database, CI passes from a clean checkout, and approved security/data invariants have PostgreSQL-backed regression coverage. Authentication itself remains a separate Phase 2 delivery gate.
