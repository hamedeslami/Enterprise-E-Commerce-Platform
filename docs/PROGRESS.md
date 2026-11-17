# Project Progress

**Current program:** NestJS migration — M4 (Atomic Refresh Token Rotation) in progress.
**Target backend:** NestJS + TypeScript + PostgreSQL + Prisma.
**Latest verified main baseline:** `f06ad9af7650bbdc2354bdd02b6e315c4addc053` (M3 merged).
**Current implementation branch:** `feat/atomic-refresh-token-rotation`.
**Current code status:** M1–M3 are merged to main. M4 implementation is complete on PR #17 and all CI jobs pass; M4 has not been merged to main.

## M4 — Atomic Refresh Token Rotation (in progress)

- Implemented versioned opaque refresh-token parsing, 256-bit secret generation, SHA-256 hash-at-rest and constant-time verification.
- Implemented the NestJS refresh-token service with explicit READ COMMITTED transactions and User → AuthSession → RefreshToken row locking.
- Implemented database-clock expiry checks, strict consumed-token replay revocation, idle-expiry sliding bounded by absolute expiry, and atomic predecessor/successor replacement.
- The PostgreSQL partial unique index and consumed/replaced check constraint require a transaction-local temporary predecessor revocation before successor insertion; the service finalizes the predecessor and clears the temporary revoked state before commit. Any failure throws and rolls back.
- Added PostgreSQL integration coverage for rotation, wrong secret, unknown token, expired token/session, inactive user, strict replay, expired-session replay rejection, concurrent duplicate requests, user-before-session lock ordering under a revocation race, and successor-insert rollback.
- No login endpoint, JWT issuance, cookie/CSRF implementation or logout endpoint is included.
- **Verification:** GitHub Actions CI run #442 passed both jobs: clean PostgreSQL migration, lint, formatting, typecheck, build, unit tests, PostgreSQL integration tests, Docker Compose build/migration, liveness/readiness/OpenAPI and readiness-failure checks. TD-MIG-004 is closed for M4; TD-MIG-008 tracks pending-auth lock-order tests required before M6.

## Repository

- Repository: https://github.com/hamedeslami/Enterprise-E-Commerce-Platform
- PR #13 merged the owner-approved migration architecture and plan.
- PR #14 merged a one-line documentation correction.
- Historical PRs #5–#12 document or implement the former .NET baseline; they are not evidence that the NestJS migration is complete.

## Owner-approved migration decisions

- Replace ASP.NET Core/.NET completely with NestJS + TypeScript.
- Do not run two API backends in parallel as a permanent architecture.
- PostgreSQL development/test data may be discarded; no production/business data migration is required.
- Create a new Prisma schema and migration history; do not convert EF Core migrations.
- PostgreSQL remains authoritative for constraints, transactions and concurrency.
- Modular Monolith; no Redis, broker, microservices or generic repository framework initially.
- UUIDv7 remains the identifier strategy; M3 adds an application generator and PostgreSQL 18 UUID-version constraints.
- Zod is the canonical runtime validation system; do not run class-validator in parallel.
- Pino is the structured logger and must have tested secret/PII redaction.
- Jest + Supertest and real PostgreSQL integration tests are the baseline.
- REST/JSON and OpenAPI remain the API contract.

## Pinned Foundation baseline

| Component | Version |
|---|---|
| Node.js | 24 LTS |
| NestJS core/common/platform | 12.5.1 |
| NestJS Swagger | 12.1.2 |
| TypeScript | 6.0.3 |
| Prisma CLI / Client / PostgreSQL adapter | 7.10.1 |
| PostgreSQL | 18 |
| Zod | 4.1.12 |
| Pino | 10.4.0 |
| Jest / ts-jest | 29.7.0 / 29.4.15 |
| Supertest | 7.1.4 |

Node 24 is selected rather than Node 26 because Prisma 7's published system requirements list supported Active/Maintenance LTS lines through Node 24; the project will not use an unverified runtime combination. Exact dependency versions are pinned in package metadata and the generated lockfile is required before M2 acceptance.

## Migration stage status

| Stage | Scope | Status |
|---|---|---|
| M1 | Architecture and migration documentation | Complete |
| M2 | NestJS Foundation | Complete (PR #15 merged) |
| M3 | Prisma schema and Identity persistence | In progress (draft PR #16) |
| M4 | Atomic refresh rotation and security regression | Not started |
| M5 | Remove legacy .NET implementation and artifacts | Not started |
| M6 | Resume Identity authentication implementation | Blocked on M1–M5 |

## M3 implementation scope

M3 adds Prisma Identity persistence and a fresh PostgreSQL migration: users, account types, roles/permissions, sessions, refresh-token records, Staff MFA persistence, Customer OTP/outbox persistence and audit events. It includes PostgreSQL constraints/triggers and integration tests. It does not implement authentication endpoints, password hashing, JWT issuance, refresh rotation, OTP/TOTP ceremonies, SMS workers, cookies, CSRF or authorization guards.

## M2 scope boundary

M2 includes the NestJS host, validated environment configuration, Prisma/PostgreSQL lifecycle, Pino redaction, request correlation, Problem Details errors, Zod pipe, OpenAPI, liveness/readiness, Docker Compose, GitHub Actions and tests.

M2 does not include Identity models, authentication endpoints, JWT, sessions, OTP, MFA, cookies, CSRF, Catalog or Inventory. The existing .NET source remains historical until the separately gated M5 removal.

## Preserved product and security decisions

- Single-Vendor store; Iran as initial target market.
- Persian default locale `fa-IR`; store timezone `Asia/Tehran`; persist instants in UTC.
- Whole-toman monetary model; explicit rounding policy remains open.
- Customer and Staff are separate persisted account types, independent of roles.
- Server-controlled `IsActive`; separate from temporary lockout and `MustChangePassword`.
- Customer password login requires a verified email or verified mobile; SMS OTP is only for an existing Customer with a previously verified mobile.
- Staff accounts are provisioned only by SuperAdmin; mandatory TOTP MFA or a single-use recovery code; temporary password change after MFA when required.
- Access JWT: RS256, unique `kid`, public-key overlap and maximum five-minute lifetime.
- Refresh token: opaque, high entropy, hash-only persistence, strict single-use rotation and strict replay revocation; no grace period.
- Account-sensitive lock order: owning user → pending authentication transaction → session rows in stable UUID order → refresh-token rows.
- Separate HttpOnly access/refresh cookies; exact credentialed CORS allow-list; independent CSRF and exact Origin validation.
- PostgreSQL transactional SMS outbox, HMAC-protected OTP challenge, quotas, bounded retries and provider failover.
- Audit privileged operations; deny by default; test IDOR and horizontal/vertical authorization.

## Working agreement

For every stage: state scope and decisions, list changed files, implement, run build/tests, report exact results, explain remaining issues, update docs/progress and wait for explicit approval before the next stage. Never claim tests passed unless actually run. No merge without explicit owner approval.
