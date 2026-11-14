# Phase 2 — Identity and Security

**Status:** In progress. Scope and initial security policy are recorded here; implementation must follow the decisions and verification gates below. This is a design/implementation record, not a security certification.

## Goals

Implement customer and staff identity on the existing API-first Modular Monolith foundation. Use one Identity module and shared session/token infrastructure, but apply different authentication policies to customers and staff. The browser-facing API uses separate HttpOnly access and refresh cookies with CSRF defenses.


## Identity persistence foundation — implementation slice 1

The first implementation slice now adds the `Ecommerce.Modules.Identity` project and registers it in the API host. It uses ASP.NET Core Identity with a module-owned EF Core context and the dedicated PostgreSQL `identity` schema. User and role constructors generate UUIDv7 identifiers; the context rejects newly added Identity users or roles whose identifiers are not UUIDv7 and maps those keys as application-generated (`ValueGeneratedNever`).

Initial Identity password/lockout defaults are configured centrally in the module:
- Minimum password length: 12.
- Digit and lowercase required; uppercase and non-alphanumeric are not mandatory.
- Identity lockout: 5 failed attempts and 15 minutes.
- Email is not globally required because the initial customer account model is phone-first capable.

The EF design-time factory and pinned `dotnet-ef` tool are included. Generate the initial migration from the repository root:

```bash
dotnet tool restore
dotnet ef migrations add InitialIdentitySchema \\
  --project src/Modules/Identity/Identity.csproj \\
  --startup-project src/Ecommerce.Api/Ecommerce.Api.csproj \\
  --context IdentityDbContext \\
  --output-dir Persistence/Migrations
```

Do not run `database update` against a shared or production database as part of migration generation. Review and commit the generated migration and model snapshot, then verify it against an isolated PostgreSQL database in CI. The initial migration, Designer and model snapshot are committed. The migration has been applied successfully by the PostgreSQL-backed integration test in CI, which verifies the migration history and Identity users table.

## Authentication policy

### Customers

Support two independent sign-in methods:

1. Username + password, with password hashing and account lifecycle handled by ASP.NET Core Identity.
2. SMS OTP sent by text message to a previously verified mobile number. **The owner explicitly confirmed SMS as the OTP delivery channel on 2026-11-14.**

OTP sign-in must not silently register a new account. Registration and phone verification are explicit flows. A login OTP is bound to the customer, normalized phone number, purpose and one challenge. A phone number that is unknown, unverified or not eligible must not be disclosed through distinguishable API responses.

### Staff (SuperAdmin, Admin, StoreManager, SupportAgent)

Require username + password followed by TOTP MFA for every staff account. SMS OTP is not an allowed staff second factor or staff-only login path. Each staff member has an individual account; shared staff credentials are prohibited. TOTP enrollment is not active until a first code is verified. Store the TOTP secret encrypted at rest and store recovery codes only as hashes; recovery codes are single-use. Staff MFA reset/recovery must be audited and must not become an unauthenticated bypass.

Do not issue a normal access/refresh session after the password step alone. Use a short-lived, purpose-limited MFA transaction state until TOTP or a valid recovery code succeeds. Only then issue the normal session cookies.

### Common session behavior

- Use ASP.NET Core Identity for password hashing, normalized usernames, account lifecycle and supported lockout primitives.
- Issue a short-lived signed JWT access token and an opaque, cryptographically random refresh token in separate HttpOnly cookies. Never return token values in JSON or place them in browser storage/URLs/logs.
- Store only a cryptographic hash of each refresh token. Rotate on every successful refresh; detect reuse of a consumed token and revoke the affected token family/session.
- Bind sessions to user and authentication method/assurance level. Logout revokes the current refresh session. Provide audited staff session revocation.
- Access token target lifetime: configurable 5–10 minutes. Refresh lifetime and absolute/idle session limits must be configurable and documented before production.
- A revoked refresh session does not instantly invalidate an already-issued stateless access JWT; the accepted residual window is bounded by access-token expiry unless a later persisted revocation check is deliberately added.
- Enforce CSRF and trusted Origin checks for cookie-authenticated unsafe requests, including refresh/logout and other state-changing auth operations.

## SMS OTP design

SMS is the confirmed OTP delivery channel. Delivery remains provider-independent: define a narrow SMS delivery interface and keep provider credentials/configuration outside source control. The specific Iranian SMS vendor is intentionally deferred until the owner selects one.

### Challenge lifecycle

1. Apply edge and application abuse controls before creating a challenge or invoking an SMS provider.
2. Normalize the Iranian mobile number to a canonical E.164 representation.
3. Generate a six-digit code using a cryptographically secure random generator.
4. Persist a challenge with user/phone reference, purpose, creation/expiry timestamps, attempt count, resend state and status.
5. Store an HMAC-SHA-256 of the code using a dedicated server-side secret, including the challenge ID and purpose in the MAC input. Never store the raw OTP. A plain unsalted hash is insufficient for a six-digit code because its search space is trivial.
6. Deliver the code through the provider adapter. Do not log the code, raw delivery payload or provider credentials.
7. Verification checks purpose, expiry, attempt budget and MAC using constant-time comparison. Challenge consumption and attempt increments are atomic in PostgreSQL. A challenge is single-use.
8. A newly issued code invalidates prior active challenges for the same user/phone/purpose according to a documented transaction rule. A code for one purpose cannot be reused for another purpose.
9. Delivery failures do not expose account existence. Provider failures are observable through redacted operational telemetry and bounded retries; retries must not send duplicate messages unexpectedly.

### Initial configurable limits

These are conservative starting defaults, not universal constants; keep them configurable and tune from observed traffic and provider constraints.

| Control | Initial default |
|---|---:|
| OTP length | 6 digits |
| Challenge lifetime | 2 minutes |
| Resend cooldown per phone | 60 seconds |
| Failed verification attempts per challenge | 5 |
| Sends per phone | 5 per rolling hour |
| Sends per phone | 10 per rolling 24 hours |

Also configure IP/network request limits, per-device/session signals where available, global provider throughput and daily spend/message budgets. Apply CAPTCHA or equivalent proof-of-human only when risk thresholds are crossed, not as a substitute for rate limiting.

### Abuse and DDoS defense

- Apply network-edge/WAF/reverse-proxy protection and request-size/connection limits. ASP.NET Core rate limiting alone is not DDoS protection.
- Use ASP.NET Core endpoint rate limiting for coarse IP/network controls, with correctly configured trusted-proxy handling.
- Enforce phone/challenge quotas and global SMS budgets through shared, atomic state in PostgreSQL so multiple API instances cannot bypass them.
- Avoid relying solely on in-memory per-process counters. If measured traffic later justifies Redis/distributed rate limiting, add it with explicit outage/fail-open/fail-closed behavior; Redis is not required for the first implementation.
- Rate-limit before expensive provider calls and before account lookup where possible. Add global provider circuit breaker, concurrency bound and spend ceiling.
- Use progressive delays/temporary challenge throttling rather than permanent account lockout that attackers can weaponize to deny service.
- Return generic request responses that do not reveal whether a phone number is registered. Keep response timing and error behavior as uniform as practical.
- Alert on unusual IP/phone fan-out, provider spend/throughput, verification failure spikes and global budget exhaustion.

## Authorization

Initial roles: SuperAdmin, Admin, StoreManager, SupportAgent, Customer. Roles are coarse groupings; endpoint policies enforce explicit permissions and resource context. Deny by default. Never trust role, user ID, ownership or authentication assurance supplied by the client. Staff-only endpoints require staff permissions and a completed MFA session. Customer endpoints enforce ownership. Seed roles idempotently; never seed a predictable production admin password. Initial staff bootstrap must use a controlled, one-time process and force credential setup.

## Initial API surface (contracts to be finalized with implementation)

- POST /api/v1/auth/customer/password/login
- POST /api/v1/auth/customer/otp/request
- POST /api/v1/auth/customer/otp/verify
- POST /api/v1/auth/staff/login
- POST /api/v1/auth/staff/mfa/verify
- POST /api/v1/auth/refresh
- POST /api/v1/auth/logout
- GET /api/v1/auth/sessions
- DELETE /api/v1/auth/sessions/{sessionId}

Do not expose endpoints before implementation. Request/response DTOs must not contain raw access/refresh tokens. Cookies are set through response headers.

## Persistence and module ownership

Identity is the first persistence-owning module and therefore introduces its own Identity DbContext, schema, EF configurations, design-time factory and initial migration. Do not create a shared commerce DbContext. Identity owns users, roles/permissions, staff TOTP enrollment/recovery material, refresh sessions/token families, OTP challenges and abuse-budget state. Use PostgreSQL constraints/indexes and transactions for single-use/rotation/quota invariants. Migrations are applied as a controlled deployment step, not automatically by every API instance at startup.

Use UUIDv7 for application-generated entity IDs. Verify that Identity entity/role IDs are generated with UUIDv7 rather than silently accepting a different Guid strategy.

## Required tests and exit criteria

- Password hash verification, username normalization, disabled/locked account behavior.
- Customer password and verified-phone OTP sign-in; unknown/unverified phone response non-enumeration.
- OTP purpose binding, expiry, resend invalidation, single use, max attempts and concurrent verification.
- Per-phone rolling quotas and global SMS budget under concurrent requests and multiple application instances.
- OTP HMAC secret absence from logs/config samples; constant-time comparison path.
- Provider failure, timeout, circuit-breaker and no-unbounded-retry behavior.
- Staff password-only login cannot obtain a normal session; valid TOTP and recovery code can; recovery code is single-use.
- TOTP enrollment requires confirmation; secret is encrypted at rest; staff recovery/reset is audited.
- Refresh rotation, concurrent refresh, consumed-token reuse detection, logout and session revocation.
- Cookie flags, CSRF token, hostile/missing Origin, CORS allow-list and cross-site topology tests.
- Role/permission matrix, horizontal/vertical escalation and IDOR tests.
- Rate-limit behavior behind configured trusted proxies.
- Release build, unit tests and PostgreSQL-backed integration tests pass in CI.

## Deferred choices

- Specific Iranian SMS provider and its delivery/template/API contract (SMS channel itself is confirmed).
- Exact production frontend/API origin topology and cookie SameSite/Domain settings.
- Edge/WAF/CDN product and production rate-limit thresholds.
- Whether to add Redis after measured need.
- Passkeys/WebAuthn; not part of this phase's initial implementation.
