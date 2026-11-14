# Delivery Roadmap

**Status:** Phase 0 approved by the owner on 2026-11-14; Phase 1 is in progress. Complete and verify each phase, report actual results, then wait for explicit user approval before moving on.

## Phase 0 — Architecture and technical design
Deliver ARCHITECTURE.md, DOMAIN-MODEL.md, DATABASE-DESIGN.md, API-DESIGN.md, SECURITY.md and ROADMAP.md. Confirm scope, module boundaries, data ownership, invariants, security and test strategy. Confirmed foundation decisions: UUIDv7 identifiers; Persian (`fa-IR`) store locale; `Asia/Tehran` store timezone with UTC persistence; PostgreSQL `ReservedQuantity` counter maintained atomically with reservation records. No production application code.
**Exit:** user approves design and resolves decisions blocking foundation/schema.

## Phase 1 — Project foundation
**Status:** In progress. Solution/module skeleton; dependency registration; configuration/options validation; PostgreSQL/EF Core; migration orchestration; structured logging; exception handling/Problem Details; OpenAPI; health checks; Docker Compose; unit/integration/architecture test infrastructure and CI build.
**Exit:** clean build; API starts locally and in Compose; DB connectivity/health checks pass; migrations apply; tests pass.

## Phase 2 — Identity and security
Identity lifecycle, initial roles/permissions, login/logout, access/refresh cookies, rotation/reuse detection, session revocation, CSRF, Origin, CORS, rate limits, security headers, ownership policies and audit events.
**Exit:** auth, CSRF, replay, role, IDOR and cookie tests pass; no unresolved critical/high security findings.

## Phase 3 — Catalog and inventory
Product types, variants, attributes, categories, brands, media metadata, inventory, movements, reservations, configurable backorder policy, concurrency-safe stock allocation, storefront browse/search and admin catalog APIs. Reservation TTL defaults to 15 minutes and is configurable at store level; unpaid expired reservations are released idempotently. Backorders are configurable per product/variant and default to disabled (`NoBackorders`); supported modes are `NoBackorders`, `AllowBackorders` and `AllowBackordersNotify`. Variant-level policy overrides the product-level policy. Products/variants may disable stock management where stock tracking is not applicable.
**Exit:** catalog/stock rules, migrations, API, authorization and concurrent stock tests pass.

## Phase 4 — Pricing, taxes and promotions
Price resolution, scheduled sale prices, market-specific tax configuration/calculation, coupons, restrictions, redemption limits and automatic promotions. Persisted and API-facing monetary amounts are whole toman integers; finalize and test the rounding rule for percentage/rate calculations before implementation.
**Exit:** deterministic whole-toman rounding/tax/stacking and concurrent coupon tests pass.

## Phase 5 — Customers and cart
Customer profiles/addresses, guest/authenticated carts, login merge, server-side recalculation and expiry.
**Exit:** ownership, merge, stale price, invalid variant and lifecycle tests pass.

## Phase 6 — Checkout and orders
Checkout orchestration/idempotency, stock reservation, order creation, immutable snapshots, state transitions, cancellation, notes and refund domain records.
**Exit:** duplicate checkout, stock race (including simultaneous requests against a final unit), backorder policy, configurable 15-minute-default reservation expiry, idempotent release, state transition and snapshot tests pass.

## Phase 7 — Payments and shipping
Provider-independent payment contracts; implement a real provider only after selection; webhook signatures/inbox/idempotency/reconciliation/refunds; shipping zones/methods/classes/shipments/tracking.
**Exit:** verified payment transitions, duplicate/replay webhook, refund-bound and shipping calculation tests pass.

## Phase 8 — Remaining commerce modules
Reviews/moderation, wishlist, basic content/SEO/settings, notification delivery, reporting read models and finalized virtual/downloadable fulfillment.
**Exit:** each module meets its definition of done with API/security/tests/docs.

## Phase 9 — Hardening and release readiness
Security/privacy/dependency review, performance/query plans/load tests, concurrency/resilience, observability, backup/restore, deployment/rollback exercises and operational runbooks.
**Exit:** release checklist signed off; residual risks documented and accepted by owner.

## Working agreement

For each phase: state scope and decisions, list changed files, implement, run build/tests, report exact commands and actual results, explain remaining issues, update docs/progress, and wait for approval. Never claim tests passed unless actually run. No fake critical providers or unexplained placeholders.

## Decisions deferred to relevant phases

- **Before Phase 3:** SKU uniqueness scope; whether fractional inventory is supported; stock behavior for grouped, virtual, downloadable and external products.
- **Before Phase 4:** whole-toman rounding rule for percentage/rate calculations; applicable Iranian tax rules and tax-inclusive/exclusive pricing.
- **Before Phase 5:** guest checkout; Iranian billing/shipping address model.
- **Before frontend integration:** exact frontend/API origin topology and cookie/CSRF/CORS configuration.
- **Before Phase 7 / Notifications / Media implementation:** initial payment, shipping, email/SMS and media-storage providers.
- **Before release:** data retention/privacy requirements and deployment-specific operational settings.

Do not block Phase 1 on decisions that do not affect the foundation. Do not silently invent deferred business rules.
