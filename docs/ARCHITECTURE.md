# Architecture

**Status:** Proposed, Phase 0. No production code before approval.

## Goals and scope

An independent, API-first e-commerce backend inspired by WooCommerce, not a WordPress plugin. Initial scope is **Single-Vendor**: one store owns catalog, stock, pricing, orders and payment configuration. Staff operate the store through admin APIs; customers use storefront/account APIs. The initial target market is **Iran**. The default store locale is Persian (`fa-IR`), the store timezone is `Asia/Tehran`, and store prices/commerce amounts are expressed in **whole toman**.

Explicitly out of scope initially: multi-vendor tenancy, seller onboarding/commissions/settlement, microservices, full WordPress CMS recreation, event streaming infrastructure and speculative distributed systems.

## Architectural style

Use a Modular Monolith: one ASP.NET Core host and one PostgreSQL database initially, with explicit business-module ownership. This preserves modularity without distributed transactions and operational overhead. Apply Clean Architecture selectively. Avoid generic repositories, default CQRS/MediatR, unnecessary interfaces, generic plugin engines and god services.

## Proposed solution layout

    src/
      Ecommerce.Api/                 # HTTP host, middleware, endpoint composition
      BuildingBlocks/                # Small cross-cutting primitives only
      Modules/
        Identity/
        Catalog/
        Inventory/
        Pricing/
        Customers/
        Cart/
        Checkout/
        Orders/
        Payments/
        Shipping/
        Taxes/
        Promotions/
        Reviews/
        Wishlist/
        Media/
        Content/
        Notifications/
        Reporting/
    tests/
      Ecommerce.UnitTests/
      Ecommerce.IntegrationTests/
      Ecommerce.ArchitectureTests/
    docs/

Use UUIDv7 identifiers consistently across modules. Generate identifiers in the application (using the platform UUIDv7 API) and store them as PostgreSQL `uuid`; do not mix identifier strategies across modules. Each module begins as one .NET project with internal Domain, Application, Infrastructure and Contracts/Endpoints folders where useful. Split into multiple projects only when a real dependency/build boundary justifies it. Each module exposes explicit registration and endpoint composition methods. Keep internals internal.

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

1. API host references module public composition/endpoint contracts, not module internals.
2. Modules communicate through narrow, explicit contracts or application-facing services.
3. No cross-module foreign keys initially. Store opaque IDs and validate through the owning module.
4. Avoid synchronous circular dependencies. Put orchestration in a coordinating use case or define a narrow contract.
5. Business invariants live with the module that owns the data.
6. BuildingBlocks contains only stable primitives, not commerce entities or business services.
7. Cross-module events are in-process initially. Use a transactional outbox only when losing an event after commit would cause business inconsistency.

## Request and persistence flow

Endpoints remain thin: authenticate/authorize, bind and validate DTOs, invoke a use case, map result to HTTP. No business logic or direct EF queries in controllers/endpoints. Never expose EF entities as API contracts. Use FluentValidation at boundaries and enforce domain invariants again in application/domain operations and database constraints.

Each module owns its DbContext, schema, EF configurations and migrations. PostgreSQL is authoritative. Apply migrations in a controlled deployment step, not concurrently on every app startup. Use explicit transactions, constraints and concurrency control. Inventory reservation must atomically increment `ReservedQuantity` and create its reservation record in the same PostgreSQL transaction. For `NoBackorders`, the update must include a database predicate equivalent to `OnHandQuantity - ReservedQuantity >= requestedQuantity`; a failed predicate means insufficient stock. Release decrements the counter only when an `Active` reservation is conditionally transitioned to `Released/Expired`. Consumption conditionally transitions an eligible active reservation and, in the same transaction, decrements both `ReservedQuantity` and `OnHandQuantity` and records the movement. Database constraints must prevent negative counters; the counter and reservation rows must never be committed separately. An expiry worker and payment handler must compete through the same conditional state transition, so only one wins. A verified payment arriving after a reservation has expired/released must follow an explicit order exception/reconciliation/refund path; it must never silently consume unavailable stock. All persisted and API-facing commerce amounts are whole toman integers: use .NET `long` and PostgreSQL `BIGINT`. Percentage/rate calculations may require a higher-precision intermediate, but every amount posted to a ledger, cart, order, refund or payment must be rounded to a whole toman using one explicit, tested rounding policy. Instants are stored as UTC (`timestamptz`); store-local schedules and presentation use the configured IANA timezone `Asia/Tehran`. The default locale is `fa-IR`; locale affects presentation and validation messages, not canonical stored money or timestamps. Payment-provider-specific rial/toman units must be converted only at the provider adapter boundary, never implicitly across the domain.

Cross-module checkout is not a distributed transaction. Use orchestration, stock reservations, idempotency and compensating actions.

## Reliability and infrastructure

Start without Redis, a broker or a job framework. Use hosted services only for simple bounded jobs. Add durable jobs/outbox when retryability across process restarts is required. Critical webhook processing, payment reconciliation, notification delivery and reservation expiry need durable state and idempotent handlers.

Redis is optional: introduce only for a measured cache, distributed rate limiter or coordination requirement, with defined expiry and failure behavior.

## Technology baseline

- .NET 10 LTS / ASP.NET Core 10 and matching stable EF Core 10.
- C# 14, as supported by the selected .NET 10 SDK.
- PostgreSQL, xUnit, Docker Compose.
- FluentValidation, Serilog and Swagger/OpenAPI where justified.
- Pin compatible stable package versions; verify support and security advisories before adding dependencies.

The owner approved .NET 10 LTS as the runtime baseline on 2026-11-14. Keep the SDK/runtime and EF Core/provider on matching supported major versions and apply monthly servicing updates.

## Quality attributes

Security: deny by default, server-side authorization, cookie authentication and CSRF defense.  
Integrity: transactional invariants, constraints, concurrency control and immutable order snapshots.  
Maintainability: module ownership, explicit contracts and small dependencies.  
Operability: structured logs, health checks, metrics, correlation and documented configuration.  
Testability: unit, PostgreSQL integration, API/security and architecture dependency tests.

## Product decisions still open

Tax rules and legal requirements for Iran; the exact rounding policy for percentage-based calculations; guest checkout; payment/shipping/email providers; frontend/API deployment topology and origins; media storage; downloadable product delivery; SKU uniqueness scope; fractional inventory; and detailed stock behavior for each product type. These are intentionally deferred to the phases that implement them. Keep provider-specific behavior behind configuration/provider boundaries, but do not invent it.
