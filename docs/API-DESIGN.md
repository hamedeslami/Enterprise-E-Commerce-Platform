# API Design

**Status:** Proposed, Phase 0/2. **Style:** REST/JSON over HTTPS. **Base path:** /api/v1.

## API surfaces

Separate storefront/customer operations from administration by route and policy.

| Surface | Example |
|---|---|
| Authentication | /api/v1/auth/* |
| Storefront | /api/v1/store/* |
| Customer account | /api/v1/account/* |
| Administration | /api/v1/admin/* |
| Payment webhooks | /api/v1/webhooks/{provider} |

Examples: GET /api/v1/store/products; GET /api/v1/store/products/{slug}; GET /api/v1/store/categories; GET/POST /api/v1/store/cart; POST /api/v1/store/checkout; GET /api/v1/account/orders; GET /api/v1/admin/orders; POST /api/v1/admin/products; PATCH /api/v1/admin/inventory/{stockItemId}. Authentication routes are listed in the Phase 2 design and become contractual only when implemented.

These are illustrative contracts, not a promise every endpoint ships in the first release. Use nouns and HTTP methods; use command endpoints only for genuine operations such as checkout, capture, refund, publish or cancel.

## HTTP and representation

- All commerce amounts (product prices, cart/order totals, taxes, discounts, payments and refunds) are whole-toman JSON integers. Fractional toman values are invalid. The API must document the amount unit explicitly; do not label toman with an ISO 4217 currency code.
- HTTPS outside local development.
- Explicit JSON DTOs; never serialize Prisma records/domain aggregates.
- Correct status codes: 200/201/202/204, 400, 401, 403, 404, 409, 422 where appropriate, 429 and 5xx.
- RFC 9457 Problem Details with stable application error codes and safe details.
- Never expose stack traces, SQL, provider secrets or sensitive data.
- UTC ISO-8601 timestamps. Store-local schedule inputs/outputs use configured `Asia/Tehran`; default user-facing locale is Persian (`fa-IR`).
- Document enum values, compatibility behavior and deprecation policy.

## Pagination, filtering and sorting

Use bounded pagination. Cursor pagination suits large/changing datasets; offset pagination may suit admin grids needing page numbers/totals. Enforce maximum page size. Filtering/sorting is allow-listed per endpoint; never concatenate client field names into SQL. Bound search length and rate-limit expensive filters.

## Validation and authorization

Validate request shape at the NestJS boundary using Zod and a small validation Pipe; enforce business invariants again in the owning module. Authorization is deny-by-default at endpoint/use-case level. Check resource ownership server-side for every customer resource. Never accept client userId, role, price, discount, stock, payment status or order owner as authoritative. Admin requires staff permissions; account routes require customer context and ownership.

## Cookie authentication contract

Access and refresh tokens are separate HttpOnly cookies and are never returned in JSON. API authenticates access cookie; refresh endpoint rotates refresh cookie and issues new access cookie. Cookie Secure, HttpOnly, SameSite, Path, Domain and expiry are configured for the real deployment topology. Do not hardcode a cookie domain. Cross-site cookies require SameSite=None; Secure and CSRF defenses.

Unsafe methods require anti-forgery validation and trusted Origin checks. Use a server-issued synchronizer-token CSRF contract with a safe bootstrap endpoint (initially `GET /api/v1/security/csrf-token`); the request token is kept in frontend memory and sent in `X-CSRF-TOKEN`. Bind it to the browser/session context and independently enforce exact Origin validation. Implementation details are tracked by TD-MIG-002. CORS uses exact allow-listed origins, never wildcard with credentials.

## Identity authentication policy (Phase 2)

- Customers may sign in with username/password or SMS OTP sent only to a previously verified mobile number.
- Staff (SuperAdmin, Admin, StoreManager, SupportAgent) must sign in with username/password and complete TOTP MFA. SMS OTP is not a staff MFA method.
- Password-only staff authentication must not issue a normal access/refresh session. MFA transaction state is short-lived and purpose-limited.
- OTP request responses must not disclose whether a phone number is registered. OTPs are purpose-bound, short-lived, single-use, attempt-limited and never returned in JSON/logs.
- Authenticated browser requests use cookie credentials and CSRF defenses. Raw access/refresh token values are never returned in response bodies or browser storage.
- See [Phase 2 Identity and Security](PHASE-2-IDENTITY-SECURITY.md) for initial OTP quotas, session behavior and verification requirements.

## Idempotency and concurrency

Support/require Idempotency-Key for checkout, payment initiation, refunds and other retry-sensitive operations. Scope keys to actor/session and operation; persist request fingerprint and result with retention. Use ETags/If-Match or concurrency tokens for admin updates where lost updates matter. Return 409 for state conflicts.

Webhook endpoints verify signatures against raw request body, apply provider replay/timestamp rules, persist provider event IDs and process idempotently. Acknowledge only after durable acceptance.

## OpenAPI and compatibility

Generate OpenAPI from actual contracts. Document cookie behavior, CSRF header, credentials, pagination, filters, error codes and idempotency. Do not document endpoints before they exist. Prefer additive changes; use a new API version for breaking changes.

## Decisions pending

Define the explicit API amount-unit field/contract for whole-toman integers; guest cart capability; Iranian address fields; frontend/API origin topology; and pagination-total requirements. SMS provider selection remains deferred until the Notifications/SMS provider decision. Persian (`fa-IR`) is the default store locale and `Asia/Tehran` is the store timezone. UUIDv7 identifiers are used consistently and serialized as canonical UUID strings in JSON.
