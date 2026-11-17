# Delivery Roadmap

**Status:** NestJS migration approved. M1 (architecture and migration documentation) is in progress on a dedicated branch. The previous .NET Phase 1 foundation and Phase 2 Identity persistence are historical implementation baselines and are scheduled for replacement; authentication implementation has not started in the target NestJS stack. Complete and verify each migration stage, report actual results, then wait for explicit user approval before moving on.

## Migration program — NestJS replacement

The owner approved complete replacement of ASP.NET Core/.NET with NestJS + TypeScript. The existing PostgreSQL data is disposable development/test data; no business data migration is required. The target stack and stage gates are defined in [NESTJS-MIGRATION-PLAN.md](NESTJS-MIGRATION-PLAN.md).

1. **M1 — Architecture/documentation:** align architecture, security, database, Identity and delivery documents with NestJS; no runtime changes.
2. **M2 — NestJS Foundation:** API host, configuration, Prisma, PostgreSQL, Pino, Zod, error handling, OpenAPI, health checks, Docker and CI.
3. **M3 — Identity persistence:** new Prisma schema/migrations and PostgreSQL constraints from an empty development/test database.
4. **M4 — Refresh rotation/security regression:** atomic PostgreSQL rotation and concurrency/security tests.
5. **M5 — Remove .NET:** delete legacy executable/build artifacts and make NestJS the sole backend.
6. **M6 — Resume Identity authentication implementation:** continue the approved Phase 2 slices only after migration acceptance.

**Migration rule:** no parallel .NET/NestJS production backends; no EF migration conversion; no destructive production database action. Each stage is a separate reviewable PR and requires owner approval before the next stage.

## Phase 0 — Architecture and technical design

Deliver ARCHITECTURE.md, DOMAIN-MODEL.md, DATABASE-DESIGN.md, API-DESIGN.md, SECURITY.md and ROADMAP.md. Confirm scope, module boundaries, data ownership, invariants, security and test strategy. Confirmed foundation decisions: UUIDv7 identifiers; Persian (`fa-IR`) locale; `Asia/Tehran` timezone with UTC persistence; PostgreSQL `ReservedQuantity` counter maintained atomically with reservation records.

**Exit:** user approves design and resolves decisions blocking foundation. **Complete.**

## Phase 1 — Project foundation (legacy .NET baseline)

**Historical status:** The original .NET foundation was implemented and verified in PR #5. It is not the target runtime and will be replaced during M2. Its CI results remain historical evidence only.

The NestJS replacement Foundation is tracked as **M2** in the migration program above. Do not treat the old .NET Foundation as completion evidence for M2.

**Exit:** M2 must independently satisfy the NestJS Foundation exit criteria.

## Phase 2 — Identity and security

**Status:** Design contracts are approved; prior .NET persistence implementation is historical and will be replaced in M3/M4. Authentication implementation has not started in NestJS. Do not continue implementing Identity on the legacy .NET stack.

- Customer password login using either email or mobile number; the selected identifier must be verified (`EmailConfirmed` or `PhoneNumberConfirmed`).
- Customer SMS OTP login only to an existing, previously verified mobile number; OTP never creates an account.
- Staff accounts are created and managed only by SuperAdmin, who sets/resets temporary passwords. Employees complete a one-time MFA enrollment invitation and then authenticate using username/password + mandatory TOTP or a single-use recovery code. After MFA, they must change the temporary password before receiving a normal session; the server persists MustChangePassword and backfills existing Staff accounts to require rotation. Password success alone never creates a normal session; the fifth failed MFA submission in 15 minutes causes a 15-minute temporary lockout.
- Server-enforced Customer/Staff account separation on every authentication path.
- Customer identifier normalization and PostgreSQL-enforced uniqueness for normalized email and canonical mobile; Staff username uniqueness.
- Shared session creation, JWT access issuance and initial opaque refresh-token persistence after a completed ceremony.
- JWT access and opaque refresh tokens in separate HttpOnly cookies; refresh rotation, reuse detection and session revocation.
- CSRF, trusted Origin, exact CORS allow-list, secure cookie policy and security headers.
- Initial roles/permissions and staff/customer resource authorization.
- OTP challenge lifecycle, HMAC storage, purpose binding, expiry, single-use and atomic attempt handling.
- PostgreSQL transactional SMS outbox with protected short-lived payload, primary/secondary provider failover, bounded retries, challenge-aligned TTL and cleanup.
- Per-phone, IP/network and global SMS quotas; edge/WAF controls, risk-based CAPTCHA, provider circuit breakers and spend budget.
- Actor-generic Customer/Staff session management, including idempotent sign-out-all endpoint DELETE /sessions.
- Auditing and security-focused tests.

**Exit:** auth, MFA, OTP, CSRF, replay, quota concurrency, role, IDOR, cookie and session tests pass; no unresolved critical/high security findings. This does not replace an independent production security review.

## Phase 3 — Catalog and inventory

Product types, variants, attributes, categories, brands, media metadata, inventory, movements, reservations, configurable backorder policy, concurrency-safe stock allocation, storefront browse/search and admin catalog APIs. Reservation TTL defaults to 15 minutes and is configurable at store level; unpaid expired reservations are released idempotently. Backorders are configurable per product/variant, disabled by default (`NoBackorders`); supported modes are `NoBackorders`, `AllowBackorders` and `AllowBackordersNotify`; variant-level policy overrides product policy. Stock management can be disabled where physical stock tracking is not applicable.

**Exit:** catalog/stock rules, migrations, API, authorization and concurrent stock tests pass.

## Phase 4 — Pricing, taxes and promotions

Price resolution, scheduled sale prices, market-specific tax configuration/calculation, coupons, restrictions, redemption limits and automatic promotions. Persisted/API monetary amounts are whole toman integers; finalize and test rounding before implementation.

**Exit:** deterministic whole-toman rounding/tax/stacking and concurrent coupon tests pass.

## Phase 5 — Customers and cart

Customer profiles/addresses, guest/authenticated carts, login merge, server-side recalculation and expiry.

**Exit:** ownership, merge, stale price, invalid variant and lifecycle tests pass.

## Phase 6 — Checkout and orders

Checkout orchestration/idempotency, stock reservation, order creation, immutable snapshots, state transitions, cancellation, notes and refund domain records.

**Exit:** duplicate checkout, stock race (including simultaneous requests against a final unit), backorder policy, reservation expiry/release, state transition and snapshot tests pass.

## Phase 7 — Payments and shipping

Provider-independent payment contracts; implement a real provider only after selection; webhook signatures/inbox/idempotency/reconciliation/refunds; shipping zones/methods/classes/shipments/tracking.

**Exit:** verified payment transitions, duplicate/replay webhook, refund-bound and shipping calculation tests pass.

## Phase 8 — Remaining commerce modules

Reviews/moderation, wishlist, basic content/SEO/settings, notification delivery, reporting read models and finalized virtual/downloadable fulfillment.

**Exit:** each module meets its definition of done with API/security/tests/docs.

## Phase 9 — Hardening and release readiness

Security/privacy/dependency review, performance/query plans/load tests, concurrency/resilience, observability, backup/restore, deployment/rollback exercises and operational runbooks.

**Exit:** release checklist signed off; residual risks documented and accepted by owner.

## Technical debt and deferred decisions

Every deferred decision must be recorded with an ID, reason, closure milestone and objective evidence in [TECHNICAL-DEBT.md](TECHNICAL-DEBT.md). Review the register at the start and end of every phase. No security or operational prerequisite may be deferred beyond its explicit gate; all rows marked before production block release until implemented and verified. A provider being selected is not closure evidence.

## Working agreement

For each migration/feature phase: state scope and decisions, list changed files, implement, run build/tests, report exact results, explain remaining issues, update docs/progress, and wait for approval. Never claim tests passed unless actually run. No fake critical providers or unexplained placeholders. Do not merge without explicit owner approval.

## Decisions deferred to relevant phases

All deferrals below are tracked with closure gates in [TECHNICAL-DEBT.md](TECHNICAL-DEBT.md); this list is an index, not a substitute for the register.

- **Phase 2:** Iranian SMS provider and message contract; production WAF/CDN/edge and trusted-proxy configuration; Redis adoption only if measured need. The logical browser topology (`shop.com`, `panel.shop.com`, `api.shop.com`) is already decided; deployment-specific TLS, origin and cookie configuration remains a production task.
- **Before Phase 3:** SKU uniqueness scope; fractional inventory; stock behavior for grouped, virtual, downloadable and external products.
- **Before Phase 4:** whole-toman rounding; applicable Iranian tax rules and tax-inclusive/exclusive pricing.
- **Before Phase 5:** guest checkout; Iranian billing/shipping address model.
- **Before Phase 7 / Notifications / Media:** initial payment, shipping, email/SMS and media-storage providers.
- **Before release:** data retention/privacy requirements and deployment-specific operational settings.

Do not block foundation work on decisions that do not affect it. Do not silently invent deferred business rules.
