# Domain Model

**Status:** Proposed, Phase 0. **Store model:** Single-Vendor.

## Domain principles

- One store is the merchant and owns catalog, inventory, pricing, orders and payment configuration.
- Each module owns its entities and invariants.
- All entity identifiers use UUIDv7, stored as PostgreSQL `uuid`. IDs crossing module boundaries are references, not permission to modify another module's state.
- API DTOs are not domain entities.
- Orders preserve immutable purchase facts. Later catalog, price, tax or address edits must not rewrite history.
- State changes use explicit operations, not arbitrary field updates.

## Core aggregates and entities

### Identity
UserAccount (login identity, normalized identifiers, password hash, account state); Role and Permission; assignments; AuthSession (device/session metadata, expiry/revocation and token family); RefreshToken (hash only, family/session, issued/expiry/consumed/revoked state and replacement reference). Customer profile belongs to Customers, not Identity. Staff/customer are authorization capabilities over accounts, not separate authentication systems.

### Catalog
Product (name, slug, descriptions, type, publication/visibility, SKU policy, brand, taxonomy and metadata); ProductVariant (variation-specific attributes, SKU, media and purchasable configuration); ProductAttribute and AttributeValue; Category (hierarchy); Brand; Tag; product-media associations. Product types include simple, variable, grouped, virtual, downloadable and external/affiliate. Each type requires explicit purchase and stock behavior.

### Inventory
StockItem (purchasable product/variant reference, stock-management flag, on-hand quantity, `ReservedQuantity` counter and effective backorder policy); StockReservation (quantity, expiry, status and checkout/order reference); StockMovement (append-only adjustment/sale/restock/reservation-release history). Stock management can be disabled for products/variants that do not require physical stock tracking. Backorder policy is configurable at product and variant level, defaults to `NoBackorders`, and supports `NoBackorders`, `AllowBackorders` and `AllowBackordersNotify`. A variant-level policy overrides the product-level policy. `ReservedQuantity` is a maintained counter equal to the sum of quantities in active reservations for that stock item. Available quantity is `OnHandQuantity - ReservedQuantity`. Reservation row creation and counter increment occur in one database transaction. When backorders are disabled, concurrent reservations must never allocate more than available stock; when enabled, overselling is permitted only as an explicit policy decision.

### Pricing, taxes and promotions
Price (whole-integer amount denominated in toman for the initial Iranian store, regular/sale values and schedule); TaxClass, TaxRate and TaxRule; Coupon (discount type/value, validity, usage limits and customer/product/category restrictions); Promotion (automatic rule with eligibility and stacking policy). Calculations are server-owned. Stored and externally represented amounts are whole toman integers. Percentage/rate calculations may use a higher-precision intermediate, then must round to a whole toman under one explicit, consistently applied and tested policy. Applicable Iranian tax rules, tax-inclusive/exclusive behavior and discount stacking must be explicitly configured. Do not treat toman as an ISO 4217 code; payment-provider unit conversion belongs in the provider adapter.

### Customers and cart
CustomerProfile linked to an Identity user when registered; guest identity is not an account. CustomerAddress with billing/shipping type. Cart (user or opaque guest owner, status, currency, expiry) and CartItem (purchasable reference, quantity, selected variant/options). Cart prices are recalculable, never authoritative order prices. Login merge revalidates stock, variant, price and promotions.

### Checkout and orders
CheckoutAttempt (idempotency key, cart, validated address/shipping/payment selections, expiry and lifecycle). Order (order number, customer/guest reference, currency, totals, status and timestamps). OrderItemSnapshot (product/variant IDs plus immutable name, SKU, attributes, unit price, quantity, tax, discount and line totals). OrderAddressSnapshot (immutable billing/shipping values). OrderNote and OrderStatusHistory. Refund (amount, reason, status and payment/order references); partial refunds cannot exceed refundable captured amount.

### Payments and shipping
PaymentAttempt and PaymentTransaction (provider, amount/currency, idempotency reference, provider transaction ID, status and timestamps). WebhookInboxItem (provider event ID, signature result, processing and retry state). ShippingZone, ShippingMethod, ShippingClass. Shipment (order reference, carrier/provider, tracking number, status and event history).

### Supporting modules
Review (product, author, rating, body, moderation and verified-purchase evidence); Wishlist and WishlistItem; MediaAsset (storage key, content type, size, dimensions, checksum and processing state); ContentPage, Menu and StoreSetting; Notification and DeliveryAttempt; ReportProjection (derived read model, not source-of-truth data).

## Critical invariants

1. Product/variant must be purchasable and valid for requested quantity at checkout.
2. Client cannot set authoritative price, tax, discount, stock, order owner, payment status or permissions.
3. SKU uniqueness scope must be explicitly decided and enforced.
4. Variable product purchase requires a valid variant.
5. Reservation and order creation are concurrency-safe and idempotent; two simultaneous requests cannot claim the same final unit when backorders are disabled.
6. The `ReservedQuantity` counter and reservation records are updated in the same transaction; only a successful conditional transition from `Active` may release or consume a reservation.
7. Backorder policy is explicit, configurable per product/variant, defaults to disabled, and variant policy overrides product policy.
8. Reservation expiry, release and consumption are idempotent; retries cannot double-release or double-consume stock. If payment is verified after expiry/release, the order enters an exception/reconciliation/refund path; expired stock is never silently reallocated.
9. Every on-hand stock adjustment produces a stock movement.
9. Order captures immutable item, address, price, discount and tax snapshots.
10. Order transitions and refund limits are validated by server-side operations.
11. Webhooks are signature-verified and idempotent; redirect alone never proves payment.
12. Coupon eligibility and usage limits are checked atomically.
13. Customer ownership comes from authenticated identity or validated guest capability, never trusted client userId.
14. Catalog edits/deletions cannot corrupt order history.

## State machines to define before implementation

- Reservation: Active, Consumed, Released, Expired. Reservation TTL defaults to 15 minutes and is configurable at store level. Persist an absolute expiry timestamp on each reservation; changing the store setting affects new reservations, not already-created ones. Expiry and payment consumption race through conditional database state transitions; exactly one transition may succeed.
- Checkout: Created, Validating, AwaitingPayment, Completed, Failed, Expired.
- Order: PendingPayment, Processing, OnHold, Completed, Cancelled, Refunded, Failed. Partial refund is an amount/record, not necessarily an order status.
- Payment: Created, RequiresAction, Authorized, Captured, Failed, Cancelled, PartiallyRefunded, Refunded.
- Shipment: Pending, Ready, Shipped, InTransit, Delivered, Exception, Returned.

Transitions require guards and history. Product publication vocabulary is to be finalized in Catalog.

## Open domain decisions

Confirm the rounding policy for percentage/rate calculations, applicable Iranian tax rules and tax-inclusive/exclusive pricing, guest checkout, downloadable product authorization/expiry, coupon stacking, review eligibility, detailed Iranian address fields and whether paid orders may be edited. SKU uniqueness scope, fractional inventory and product-type stock behavior must be finalized before Catalog/Inventory implementation. UUIDv7, `fa-IR`, `Asia/Tehran`, the 15-minute default reservation TTL, backorder modes and the `ReservedQuantity` counter strategy are confirmed. Do not guess the remaining rules.
