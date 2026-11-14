# Database Design

**Status:** Proposed, Phase 0. **Database:** PostgreSQL. **ORM:** EF Core 11.

## Ownership and schemas

Use one PostgreSQL database initially with a dedicated schema per module: identity, catalog, inventory, pricing, customers, cart, orders, payments, shipping, taxes, promotions, reviews, wishlist, media, content, notifications and reporting.

Each module owns its DbContext, tables, EF configurations, migrations and transaction boundaries. Apply migrations in a deterministic deployment step, not independently from every app replica. Avoid cross-module foreign keys initially; use opaque IDs and validate through owning module contracts. Within a module, use real FK, unique, check constraints and deliberate delete behavior.

## Relational modeling guidance

- Identity: users, roles, permissions, assignments, sessions and refresh-token records. Unique normalized login identifiers; index user/session/family and token hash. Never persist raw tokens.
- Catalog: products, variants, categories, category hierarchy, attributes/values, assignments, brands, tags and join tables. Define SKU and slug uniqueness scope. Prevent duplicate/conflicting variant attribute combinations. Avoid dozens of nullable type-specific columns and avoid generic JSON as the primary model.
- Inventory: stock items, reservations with expiry/status, append-only movements, and explicit stock-management/backorder policy. Stock management can be disabled where tracking is not applicable. Backorder policy is configurable per product/variant, defaults to `NoBackorders`, and supports `NoBackorders`, `AllowBackorders` and `AllowBackordersNotify`; a variant override takes precedence over the product default. Define the effective policy in the inventory/catalog contract rather than silently inheriting inconsistent defaults. Use quantity types appropriate to discrete or fractional stock. Atomic conditional updates, row locks or equivalent concurrency control prevent overselling when backorders are disabled. Reservation creation and available-stock validation must be one atomic database operation; application-memory locks are insufficient across API replicas.
- Pricing/taxes/promotions: numeric monetary values with currency; sale schedules; jurisdiction/effective-date tax rules; normalized unique coupon codes and transactional redemption records.
- Customers/cart: customer profiles and addresses; carts and items; guest carts use high-entropy opaque capabilities, not login tokens. Index owner/status/expiry and cart item lookups.
- Orders/payments: orders, item/address snapshots, notes, status history, refunds, payment attempts/transactions, webhook inbox and idempotency records. Preserve product name, SKU, variant selection, unit price, currency, tax/discount breakdown and totals.
- Shipping/supporting: zones/methods/classes, shipments/tracking events, reviews, wishlist, media metadata, content pages, notification attempts and reporting projections.

## Constraints and indexes

Choose one identifier strategy (UUID/ULID or bigint identity) before migrations and use consistent FK naming. Add unique indexes for normalized login, SKU, scoped slug, coupon code, provider event IDs and idempotency keys. Add check constraints for positive quantities, coherent stock counters, valid date ranges and applicable non-negative amounts. Add composite indexes based on actual access paths, such as owner/status, status/created_at and reservation status/expiry. Use partial indexes only when query patterns justify them.

Do not index every column speculatively. Validate query plans using representative data.

## Transactions and concurrency

- Keep transactions within a module wherever possible.
- Checkout is an orchestration workflow, not a distributed transaction: validate cart/selections; calculate authoritative totals; reserve stock; create pending order with snapshots; initiate payment; consume or release reservations based on verified outcome and timeout policy.
- Every externally retried operation needs an idempotency boundary.
- Stock reservation uses atomic SQL predicates, row locking or equivalent concurrency-safe logic, tested with concurrent PostgreSQL requests, including two or more buyers racing for the final unit.
- When backorders are disabled, the database operation must reject allocation that would make available stock negative. When enabled, oversell is an explicit policy outcome and must remain observable in stock/order state.
- Reservation expiry/release and reservation consumption must be idempotent; a retry must not release or consume the same reservation twice. Default reservation TTL is 15 minutes and is configurable at store level; persist the expiry instant per reservation so later setting changes do not silently rewrite existing reservations.
- Coupon usage counters/records update atomically.
- Webhook inbox uniqueness prevents duplicate application.
- Use transactional outbox only when a committed business change must reliably publish an event.

## Types and time

For the initial Iranian store, canonical product, cart, order, payment and refund amounts are whole integers denominated in toman. Store them as PostgreSQL `BIGINT` mapped to .NET `long`; expose them as JSON integers. Do not use float/double or decimal columns for posted monetary amounts. Percentage/rate calculations may use a higher-precision intermediate, but the resulting amount must be rounded to a whole toman before persistence or API output, using one explicit and tested rounding policy. Toman is a business unit, not an ISO 4217 currency code; do not label it with an ISO code. If a payment provider expects rial or another unit, convert explicitly at the provider adapter boundary and test the factor/direction to prevent 10x errors. Define rounding rules for each calculation. Store instants as timestamptz in UTC; use a configured IANA timezone only for store-local schedules and presentation. Define quantity precision according to supported goods. JSONB is acceptable for bounded provider metadata or validated non-core extension data, not as a substitute for relational commerce modeling.

Soft delete only when restoration/audit requirements justify it. Orders, payments, stock movements and audit history must not be silently hard-deleted.

## Migrations and deployment

Each module owns migrations. Migration generation/application are reviewed artifacts. CI applies migrations to disposable PostgreSQL. Production migration execution is a controlled release step with backup and rollback strategy; not every schema change is safely reversible.

## Decisions pending

Identifier format, percentage-calculation rounding policy, SKU scope, fractional inventory, store timezone, Iranian address model, data retention/privacy and read-replica requirements.
