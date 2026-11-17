# Architecture

**Status:** Approved direction for the NestJS migration. Implementation is gated by the migration plan and phase approvals.

## Goals and scope

An independent, API-first e-commerce backend inspired by WooCommerce, not a WordPress plugin. Initial scope is **Single-Vendor**: one store owns catalog, stock, pricing, orders and payment configuration. Staff operate the store through admin APIs; customers use storefront/account APIs. The initial target market is **Iran**. The default store locale is Persian (`fa-IR`), the store timezone is `Asia/Tehran`, and store prices/commerce amounts are expressed in **whole toman**.

Explicitly out of scope initially: multi-vendor tenancy, seller onboarding/commissions/settlement, microservices, full WordPress CMS recreation, event streaming infrastructure and speculative distributed systems.

## Architectural style

Use a **Modular Monolith**: one NestJS application/API host and one PostgreSQL database initially, with explicit business-module ownership. This preserves modularity without distributed transactions and operational overhead. Apply Clean Architecture selectively. Avoid generic repositories, default CQRS/MediatR-style frameworks, unnecessary interfaces, generic plugin engines and god services.

The selected runtime is Node.js LTS supported by the pinned NestJS release and TypeScript toolchain. Pin compatible stable versions and update them deliberately. The application is TypeScript-first; do not introduce a second backend runtime or retain ASP.NET Core as a parallel service.

## Proposed solution layout

    src/
      main.ts
      app.module.ts
      shared/                         # small cross-cutting primitives only
        config/
        database/
        logging/
        http/
        validation/
        security/
      modules/
        identity/
        catalog/
        inventory/
        pricing/
        customers/
        cart/
        checkout/
        orders/
        payments/
        shipping/
        taxes/
        promotions/
        reviews/
        wishlist/
        media/
        content/
        notifications/
        reporting/
    prisma/
      schema.prisma
      migrations/
    test/
    docs/

Each business module owns its application behavior and persistence boundary. Use Nest modules/providers/controllers as composition and delivery mechanisms, not as a reason to force every class into an interface or duplicate layers. A module may use internal domain, application, infrastructure and presentation folders where the complexity justifies them. Split modules into separate packages only when a real dependency/build boundary justifies it. Keep module internals private.

Use UUIDv7 identifiers consistently across modules and store them as PostgreSQL `uuid`. Generate identifiers in the application using a reviewed Node.js-compatible UUIDv7 implementation; verify runtime/library support and preserve UUIDv7 behavior in tests. UUIDs are identifiers, not authorization controls.

## Module responsibilities

| Module | Responsibility |
|---|---|
| Identity | Accounts, credentials, roles, permissions, sessions and refresh-token families |
| Catalog | Products, variants, categories, tags, brands, attributes and visibility |
| Inventory | Stock, reservations, movements, configurable backorder policies and concurrency-safe allocation |
| Pricing | Regular/sale/scheduled prices and price resolution |
| Customers | Customer profiles, addresses, notes and groups |
| Cart | Guest/authenticated carts, items, expiry and merge |
| Checkout | Checkout orchestration and validation across modules |
| Orders | Orders, purchase snapshots, notes, state transitions and refund records |
| Payments | Payment attempts, transactions, provider configuration and webhook inbox |
| Shipping | Zones, methods, classes, shipments and tracking |
| Taxes | Tax classes, rates, rules and calculation |
| Promotions | Coupons, restrictions, usage limits and automatic promotions |
| Reviews | Product reviews, ratings and moderation |
| Wishlist | Customer wishlists and entries |
| Media | Media metadata and storage-provider boundary |
| Content | Basic pages, SEO metadata, menus and store settings |
| Notifications | Templates, delivery requests and provider adapters |
| Reporting | Read-oriented reporting queries and projections |

Checkout coordinates; it does not own product, stock, price, payment or order state. Inventory reservations default to a 15-minute TTL, configurable at store level; expiry processing releases unpaid reservations idempotently. Inventory is authoritative for stock allocation: simultaneous checkout attempts must not reserve/sell the same final unit when backorders are disabled. Backorder policy is configurable per product/variant, defaults to disabled, and supports `NoBackorders`, `AllowBackorders` and `AllowBackordersNotify`; a variant-level setting overrides the product-level setting. Stock tracking itself can be disabled for products/variants where physical stock is not applicable. Reporting uses explicit query contracts/read models, not direct access to another module's tables.

## Dependency rules

1. The NestJS application host composes modules through their exported module providers/contracts; it must not reach into module internals.
2. Modules communicate through narrow, explicit contracts or application-facing services.
3. No cross-module foreign keys initially. Store opaque IDs and validate through the owning module.
4. Avoid synchronous circular dependencies. Put orchestration in a coordinating use case or define a narrow contract.
5. Business invariants live with the module that owns the data.
6. `shared/` contains only stable primitives, not commerce entities or business services.
7. Cross-module events are in-process initially. Use a transactional outbox only when losing an event after commit would cause business inconsistency.

## Request, validation and persistence flow

Controllers remain thin: authenticate/authorize, parse and validate transport input, invoke an application operation, and map its result to HTTP. Business logic does not live in controllers. Do not expose Prisma records as API contracts.

Use Zod schemas as the canonical runtime validation and input-contract mechanism. Integrate them through a small NestJS validation pipe/adapter; do not run Zod and class-validator as competing validation systems. Validate transport shape at the boundary and enforce business invariants again in application/domain operations and database constraints.

Use Prisma Client for ordinary persistence. Keep schema and migrations explicit and reviewed. Prisma must not hide database invariants or replace transaction design. Use parameterized raw SQL where PostgreSQL-specific behavior is required, including row locking, conditional updates, partial indexes or other features not expressed adequately through Prisma. Review every raw query. Apply migrations in a controlled deployment step, not automatically on every API replica startup.

Each module owns its tables and data rules within the shared PostgreSQL database. A single Prisma schema/migration history is acceptable for the initial monolith; module ownership is enforced through code boundaries, naming and review rather than pretending Prisma provides independent per-module migration engines. Avoid cross-module foreign keys initially; within a module use real foreign keys, unique/check constraints and deliberate delete behavior.

PostgreSQL is authoritative. Use explicit transactions, constraints and concurrency control. Inventory reservation must atomically increment `ReservedQuantity` and create its reservation record in the same transaction. For `NoBackorders`, the update must include a database predicate equivalent to `OnHandQuantity - ReservedQuantity >= requestedQuantity`; a failed predicate means insufficient stock. Release decrements the counter only when an `Active` reservation is conditionally transitioned to `Released/Expired`. Consumption conditionally transitions an eligible active reservation and, in the same transaction, decrements both `ReservedQuantity` and `OnHandQuantity` and records the movement. Database constraints must prevent negative counters; the counter and reservation rows must never be committed separately. An expiry worker and payment handler must compete through the same conditional state transition, so only one wins. A verified payment arriving after a reservation has expired/released must follow an explicit order exception/reconciliation/refund path; it must never silently consume unavailable stock.

All persisted and API-facing commerce amounts are whole toman integers. Use TypeScript `bigint` for exact in-process representation where values may exceed JavaScript's safe integer range, and PostgreSQL `BIGINT` for storage. Define an explicit JSON serialization contract because JSON does not natively serialize `bigint`; do not silently coerce potentially unsafe values to JavaScript `number`. Percentage/rate calculations may require a higher-precision intermediate, but every amount posted to a ledger, cart, order, refund or payment must be rounded to a whole toman using one explicit, tested rounding policy. Instants are stored as UTC (`timestamptz)); store-local schedules and presentation use the configured IANA timezone `Asia/Tehran`. The default locale is `fa-IR`; locale affects presentation and validation messages, not canonical stored money or timestamps. Payment-provider-specific rial/toman units must be converted only at the provider adapter boundary, never implicitly across the domain.

## Reliability and infrastructure

Start without Redis, a message broker or a job framework. Use bounded NestJS/Node worker processes only for simple jobs whose loss on process restart is acceptable. Critical webhook processing, payment reconciliation, notification delivery and reservation expiry need durable state and idempotent handlers; use PostgreSQL-backed outbox/leases where appropriate.

Redis is optional: introduce only for a measured cache, distributed rate limiter or coordination requirement, with defined expiry and failure behavior. Do not add it preemptively.

## Technology baseline

- NestJS + TypeScript on a supported Node.js LTS runtime.
- PostgreSQL 18 as the primary database.
- Prisma ORM and Prisma Migrate for schema/client/migration management.
- Zod for runtime validation, integrated through a small NestJS pipe.
- Pino for structured logging with explicit redaction.
- Jest for unit tests; Supertest for HTTP/integration tests.
- PostgreSQL-backed integration tests; no SQLite substitution for concurrency/security behavior.
- Docker Compose and GitHub Actions.
- OpenAPI/Swagger generated from the actual HTTP contracts.

Pin compatible stable versions, commit the lockfile, use reproducible installs in CI, and review dependency/security advisories before adding or upgrading packages. Exact versions are selected during the Foundation migration and recorded in package metadata/lockfile; this architecture does not invent unverified version numbers.

## Quality attributes

Security: deny by default, server-side authorization, cookie authentication and CSRF defense.  
Integrity: transactional invariants, constraints, concurrency control and immutable order snapshots.  
Maintainability: module ownership, explicit contracts and small dependencies.  
Operability: structured logs, health checks, metrics, correlation and documented configuration.  
Testability: unit, PostgreSQL integration, API/security and architecture dependency tests.

## Product decisions still open

Tax rules and legal requirements for Iran; the exact rounding policy for percentage-based calculations; guest checkout; payment/shipping/email providers; frontend/API deployment topology and origins; media storage; downloadable product delivery; SKU uniqueness scope; fractional inventory; and detailed stock behavior for each product type. These are intentionally deferred to the phases that implement them. Keep provider-specific behavior behind configuration/provider boundaries, but do not invent it.
