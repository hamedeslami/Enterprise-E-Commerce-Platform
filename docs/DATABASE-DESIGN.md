# Database Design

**Status:** Proposed, Phase 0. **Database:** PostgreSQL. **ORM:** Prisma.

## Identifiers

Use UUIDv7 for all entity primary keys and cross-module identifiers. Generate IDs in the application using the platform UUIDv7 implementation and map them to PostgreSQL `uuid` / TypeScript `string` (validated UUID). Use the same strategy consistently for primary and foreign-key columns. UUIDv7 provides time-ordered identifiers without exposing sequential database identity values; it is not an authorization mechanism.

## Ownership and schemas

Use one PostgreSQL database initially with a dedicated schema per module: identity, catalog, inventory, pricing, customers, cart, orders, payments, shipping, taxes, promotions, reviews, wishlist, media, content, notifications and reporting.

Each module owns its tables, constraints, data rules and transaction boundaries. Prisma schema and migrations are centrally managed for the initial monolith; module ownership is enforced through code boundaries and review. Apply migrations in a deterministic deployment step, not independently from every app replica. Avoid cross-module foreign keys initially; use opaque IDs and validate through owning module contracts. Within a module, use real FK, unique, check constraints and deliberate delete behavior.

## Relational modeling guidance

- Identity: users, roles, permissions, assignments, sessions, refresh-token records and the supporting OTP/MFA persistence. Customer normalized email/mobile use Customer-only partial unique indexes; Staff username remains globally unique. Never persist raw tokens. The NestJS/Prisma model and PostgreSQL-specific constraints are detailed in [IDENTITY-PERSISTENCE-DESIGN.md](IDENTITY-PERSISTENCE-DESIGN.md).
- Catalog: products, variants, categories, category hierarchy, attributes/values, assignments, brands, tags and join tables. Define SKU and slug uniqueness scope. Prevent duplicate/conflicting variant attribute combinations. Avoid dozens of nullable type-specific columns and avoid generic JSON as the primary model.
- Inventory: stock items with `OnHandQuantity` and `ReservedQuantity` counters, reservations with expiry/status, append-only movements, and explicit stock-management/backorder policy. Stock management can be disabled where tracking is not applicable. Backorder policy is configurable per product/variant, defaults to `NoBackorders`, and supports `NoBackorders`, `AllowBackorders` and `AllowBackordersNotify`; a variant override takes precedence over the product default. Define the effective policy in the inventory/catalog contract rather than silently inheriting inconsistent defaults. Use quantity types appropriate to discrete or fractional stock. Use an atomic conditional update to reserve quantity and increment `ReservedQuantity` in the same transaction that inserts the reservation row. With `NoBackorders`, the update predicate must ensure `OnHandQuantity - ReservedQuantity >= requestedQuantity`; with a backorder policy, reservation is allowed according to that explicit policy. Application-memory locks are insufficient across API replicas.
- Pricing/taxes/promotions: whole-toman integer monetary values mapped to PostgreSQL `BIGINT` / TypeScript `bigint`; sale schedules; jurisdiction/effective-date tax rules; normalized unique coupon codes and transactional redemption records.
- Customers/cart: customer profiles and addresses; carts and items; guest carts use high-entropy opaque capabilities, not login tokens. Index owner/status/expiry and cart item lookups.
- Orders/payments: orders, item/address snapshots, notes, status history, refunds, payment attempts/transactions, webhook inbox and idempotency records. Preserve product name, SKU, variant selection, unit price, currency, tax/discount breakdown and totals.
- Shipping/supporting: zones/methods/classes, shipments/tracking events, reviews, wishlist, media metadata, content pages, notification attempts and reporting projections.

## Constraints and indexes

Use UUIDv7 consistently for primary and foreign-key identifiers; PostgreSQL column type is `uuid` and the application representation is a validated UUID string. Add unique indexes for normalized login, SKU, scoped slug, coupon code, provider event IDs and idempotency keys. Add check constraints for positive quantities, coherent stock counters, valid date ranges and applicable non-negative amounts. Add composite indexes based on actual access paths, such as owner/status, status/created_at and reservation status/expiry. Use partial indexes only when query patterns justify them.

Do not index every column speculatively. Validate query plans using representative data.

## Transactions and concurrency

- Keep transactions within a module wherever possible.
- Checkout is an orchestration workflow, not a distributed transaction: validate cart/selections; calculate authoritative totals; reserve stock; create pending order with snapshots; initiate payment; consume or release reservations based on verified outcome and timeout policy.
- Every externally retried operation needs an idempotency boundary.
- Stock reservation uses a single atomic PostgreSQL conditional update to increment `ReservedQuantity` and inserts the matching reservation row in the same transaction. For `NoBackorders`, the update predicate rejects any request that would make available quantity negative. This is tested with concurrent PostgreSQL requests, including two or more buyers racing for the final unit.
- The counter invariant is: `ReservedQuantity = SUM(quantity of Active reservations)` for the stock item. All reservation creation, release, expiry and consumption operations update the counter and reservation state atomically. Add a database check constraint that `ReservedQuantity >= 0`; add reconciliation/diagnostic tests or operational checks to detect counter drift.
- Releasing/expiring a reservation conditionally changes only an `Active` row and decrements `ReservedQuantity` once. Consuming a reservation conditionally changes only an eligible `Active` row and, in the same transaction, decrements both `ReservedQuantity` and `OnHandQuantity` and records the stock movement. Retries must not double-release or double-consume.
- The expiry worker and payment confirmation handler race through the same conditional reservation state transition. If payment is verified after the reservation has expired or been released, do not restore/consume stock implicitly; move the order to an exception/reconciliation/refund workflow.
- Default reservation TTL is 15 minutes and is configurable at store level; persist the absolute expiry instant per reservation so later setting changes do not rewrite existing reservations.
- Coupon usage counters/records update atomically.
- Webhook inbox uniqueness prevents duplicate application.
- Use transactional outbox only when a committed business change must reliably publish an event.

## Types and time

For the initial Iranian store, canonical product, cart, order, payment and refund amounts are whole integers denominated in toman. Store them as PostgreSQL `BIGINT` represented internally as TypeScript `bigint`; expose them as JSON integers. Do not use float/double or decimal columns for posted monetary amounts. Percentage/rate calculations may use a higher-precision intermediate, but the resulting amount must be rounded to a whole toman before persistence or API output, using one explicit and tested rounding policy. Toman is a business unit, not an ISO 4217 currency code; do not label it with an ISO code. If a payment provider expects rial or another unit, convert explicitly at the provider adapter boundary and test the factor/direction to prevent 10x errors. Define rounding rules for each calculation. Store instants as `timestamptz` in UTC. The confirmed store timezone is the IANA zone `Asia/Tehran`; use it only for store-local schedules and presentation. The default store locale is Persian (`fa-IR`); do not store localized display strings as canonical domain values. Define quantity precision according to supported goods. JSONB is acceptable for bounded provider metadata or validated non-core extension data, not as a substitute for relational commerce modeling.

Soft delete only when restoration/audit requirements justify it. Orders, payments, stock movements and audit history must not be silently hard-deleted.

## Migrations and deployment

Each module owns migrations. Migration generation/application are reviewed artifacts. CI applies migrations to disposable PostgreSQL. Production migration execution is a controlled release step with backup and rollback strategy; not every schema change is safely reversible.

## Decisions pending

Percentage-calculation rounding policy; SKU uniqueness scope; fractional inventory; Iranian address model; data retention/privacy; read-replica requirements; and product-type-specific stock behavior. UUIDv7 identifiers, `fa-IR` locale, `Asia/Tehran` timezone and the `ReservedQuantity` counter strategy are confirmed.
