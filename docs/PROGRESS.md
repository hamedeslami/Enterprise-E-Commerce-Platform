# Project Progress

**Current program:** NestJS migration — M1 (architecture/documentation).
**Target backend:** NestJS + TypeScript + PostgreSQL + Prisma.
**Owner decisions:** complete .NET replacement; no hybrid backend; existing PostgreSQL data is disposable development/test data and will be recreated from Prisma migrations.
**Current code status:** the repository still contains the legacy .NET implementation on `main`. No NestJS runtime implementation has been merged yet. The migration branch is documentation-only.

## Repository

- Repository: https://github.com/hamedeslami/Enterprise-E-Commerce-Platform
- Latest previously verified main baseline: `9747756ec563a7d33d39de2574a1d8be494b1a90`.
- Previous PRs #5–#12 are historical .NET implementation/design work; they are not evidence that the NestJS migration has been completed.
- Current migration work is isolated in `docs/nestjs-migration-plan`; do not merge until the documentation review and CI are complete and the owner approves.

## Owner-approved migration decisions

- Replace ASP.NET Core/.NET completely with NestJS + TypeScript.
- Do not run two API backends in parallel as a permanent architecture.
- PostgreSQL development/test data may be discarded; no production/business data migration is required.
- Create a new Prisma schema and migration history; do not convert EF Core migrations.
- PostgreSQL remains the authoritative relational database.
- Modular Monolith remains the architectural style.
- UUIDv7 remains the identifier strategy.
- No Redis, broker, microservices or generic repository framework initially.
- Zod is the canonical runtime validation system, integrated with NestJS through a small Pipe; do not run class-validator as a parallel system.
- Pino is the structured logger and must have tested secret/PII redaction.
- Jest + Supertest and real PostgreSQL integration tests are the baseline.
- REST/JSON and OpenAPI remain the API contract.

## Migration stage status

| Stage | Scope | Status |
|---|---|---|
| M1 | Architecture and migration documentation | In progress |
| M2 | NestJS Foundation | Not started |
| M3 | Prisma schema and Identity persistence | Not started |
| M4 | Atomic refresh rotation and security regression | Not started |
| M5 | Remove .NET implementation and artifacts | Not started |
| M6 | Resume Identity authentication implementation | Blocked on M1–M5 |

See [NESTJS-MIGRATION-PLAN.md](NESTJS-MIGRATION-PLAN.md) for scope and exit criteria.

## Historical .NET baseline

The previous .NET Foundation was implemented in PR #5; Identity persistence and refresh-token rotation were added in PRs #6–#9; security/authentication design was documented in PRs #7–#12. These remain useful as historical implementation and approved-contract references, but all runtime-specific implementation will be recreated in NestJS. No authentication endpoints, JWT issuance, browser login cookies, OTP challenge worker, Staff MFA enrollment/TOTP or recovery-code flow had been implemented in the previous baseline.

## Preserved product and security decisions

- Single-Vendor store; Iran as initial target market.
- Persian default locale `fa-IR`; store timezone `Asia/Tehran`; persist instants in UTC.
- Whole-toman monetary model; explicit rounding policy remains open.
- Customer and Staff are separate persisted account types, independent of roles.
- Server-controlled `IsActive` state; separate from temporary lockout and `MustChangePassword`.
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
