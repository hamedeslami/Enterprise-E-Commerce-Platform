# Phase 2 — Identity and Security

**Status:** In progress. This is an implementation progress and navigation document, not a security certification. The detailed approved security baseline is in [SECURITY.md](SECURITY.md), the session/token contract is in [SESSION-TOKEN-DESIGN.md](SESSION-TOKEN-DESIGN.md), and the authentication ceremony contract is in [SLICE-2C-AUTHENTICATION-DESIGN.md](SLICE-2C-AUTHENTICATION-DESIGN.md).

## Goals

Implement Customer and Staff identity on the API-first Modular Monolith foundation. Use one Identity module and shared session/token infrastructure, with distinct Customer and Staff authentication policies. The browser-facing API uses separate HttpOnly access and refresh cookies with CSRF defenses.

## Delivered foundations

### Identity persistence

The previous .NET baseline implemented Identity persistence with ASP.NET Core Identity and EF Core in the PostgreSQL `identity` schema. This implementation is historical and will be replaced in M3 by NestJS application-owned Identity models persisted through Prisma. User and role identifiers remain UUIDv7; the Prisma schema and PostgreSQL constraints must reject invalid identifier strategy.

Initial Identity password/lockout defaults:
- Minimum password length: 12.
- Digit and lowercase required; uppercase and non-alphanumeric are not mandatory.
- Identity lockout: 5 failed attempts and 15 minutes.
- A dedicated database uniqueness constraint on the application's canonical Staff username remains the uniqueness boundary. Customer login must not use username as an identifier.

The EF design-time factory, `dotnet-ef` tool and EF migration history are legacy artifacts and will not be ported. M3 creates a fresh Prisma migration history and verifies it against an empty PostgreSQL database.

### Account classification

Every persisted user has an explicit, server-controlled `AccountType` of Customer or Staff. It is independent of roles. No implicit default or automatic classification is permitted. PostgreSQL constraints reject invalid/unspecified values.

### Session and refresh persistence

PR #9 adds UUIDv7 `AuthSession` and `RefreshToken` persistence, database constraints, a partial unique index for one active refresh token per session, and PostgreSQL transaction-based atomic rotation. Rotation uses session-first/token-second locking, constant-time secret verification, database time, conditional token consumption and atomic successor insertion. A valid consumed-token replay revokes only its owning session; wrong-secret attempts do not revoke.

These delivered foundations do not mean that authentication endpoints, access JWT issuance, cookies, CSRF integration, OTP challenges or Staff MFA have been implemented.

## Authentication policy — approved

### Customers

- Password login accepts either email or mobile number through one `identifier` field.
- Email password login requires `EmailConfirmed == true`.
- Mobile password login requires `PhoneNumberConfirmed == true`.
- SMS OTP is available only for an existing Customer with a previously verified mobile number.
- OTP login never creates an account. Registration and verification remain explicit separate flows.
- Customer endpoints reject Staff accounts.

### Staff

- Staff accounts are created only by SuperAdmin; self-registration is prohibited. SuperAdmin sets initial passwords and may reset them through audited privileged operations. Both are temporary credentials and set a server-controlled, non-null `MustChangePassword` flag; its migration backfills existing Staff to true and Customers to false.
- Staff login requires username/password followed by mandatory TOTP; a single-use recovery code is the controlled alternative.
- SuperAdmin issues a one-time MFA enrollment invitation; the employee redeems it with their credentials, receives the TOTP setup secret directly, confirms the first code, and receives recovery codes once. SuperAdmin never sees the authenticator secret/recovery codes.
- Password success creates only a short-lived MFA transaction; it never creates a normal session or access/refresh credentials. Staff cannot log in before MFA enrollment is complete. After successful MFA, a Staff member with `MustChangePassword=true` receives only a restricted 10-minute password-change credential; no normal session is issued until password change succeeds. The password update, flag clear, restricted-credential consumption, session and initial refresh insertion are atomic.
- The fifth failed MFA submission within a rolling 15-minute account window causes a 15-minute temporary authentication lockout across transactions/devices.
- SMS OTP is not a Staff factor.
- Staff endpoints reject Customer accounts.

Identifier normalization, database uniqueness, endpoint contracts, session creation, TOTP replay prevention, OTP abuse controls and required tests are specified in [SLICE-2C-AUTHENTICATION-DESIGN.md](SLICE-2C-AUTHENTICATION-DESIGN.md).

## Common session and browser policy

- Access JWT maximum lifetime: five minutes.
- Refresh credentials are opaque, cryptographically random, stored only as hashes and rotated atomically.
- Strict refresh replay: a valid consumed-token replay revokes its owning session; no grace period.
- Customer sessions: 30-day absolute / 7-day idle. Staff sessions: 12-hour absolute / 30-minute idle.
- Absolute expiry never slides; idle expiry extends only after successful refresh.
- `IsActive` is a non-null, server-controlled account lifecycle flag, independent of temporary application lockout and `MustChangePassword`. Existing accounts are backfilled active; new account creation must explicitly set it. Inactive accounts cannot authenticate, complete pending OTP/MFA, create sessions or refresh. Deactivation atomically invalidates pending authentication transactions and revokes active sessions/refresh credentials; reactivation does not restore them. Already-issued stateless JWTs retain only the bounded residual validity accepted by AR-SEC-001.
- Account-sensitive authentication and deactivation use the same database lock order: user → pending OTP/MFA transaction → session rows (stable UUID order) → refresh-token rows. Refresh rotation must adopt user → session → token and revalidate `IsActive` under the user-row lock.
- Access JWT contains a signed `account_type` claim with exactly `customer` or `staff`, derived from persisted `AccountType`, separate from role claims. Authorization policies enforce both account type and role; no per-request database lookup is introduced.
- Access and refresh credentials are separate HttpOnly cookies; never return raw tokens in JSON or place them in browser storage, URLs or logs.
- Enforce antiforgery request-token validation and independent exact-Origin validation for unsafe cookie-authenticated requests.
- Browser topology: `https://shop.com`, `https://panel.shop.com`, `https://api.shop.com`; exact credentialed CORS allow-list and host-only API cookies.

## OTP policy

- SMS delivery channel is confirmed; the vendor remains provider-independent and unselected.
- Six-digit cryptographically random code; two-minute expiry; 60-second resend cooldown; five failed attempts per challenge.
- Five sends per normalized phone per rolling hour and ten per rolling 24 hours.
- HMAC-SHA-256 with a dedicated external secret; challenge ID and purpose included in MAC input.
- Challenge creation, resend invalidation, attempts, quota consumption and transactional outbox insertion must be atomic in PostgreSQL.
- PostgreSQL SMS outbox uses protected short-lived payloads, primary/secondary provider failover, provider-specific circuit breakers, bounded retries/backoff, challenge-aligned expiry and cleanup.
- Shared per-phone/global quotas, IP/network limits, risk-based CAPTCHA, bounded concurrency and spend ceiling.

## Implementation sequence

1. Slice 2C design — document and approve contracts before code.
2. Shared session creation and token issuance foundation, including `IsActive` migration/backfill and account-type JWT claim/policy.
3. Customer email/mobile password and verified-mobile OTP flows, including PostgreSQL outbox and provider failover.
4. SuperAdmin Staff provisioning/password management and employee MFA enrollment/reset; Staff password pre-authentication, TOTP, account lockout and recovery.
5. Cookie/CSRF/Origin/CORS integration, authorization and Customer/Staff session-management endpoints including sign-out-all.
6. Security review, concurrency verification and CI.

Each slice is separately reviewable. Do not proceed to the next slice until the current slice's tests and review are complete and the owner approves proceeding. No PR is merged without explicit owner approval.

## Required verification and Phase 2 exit

- Identifier normalization, Customer-only database uniqueness and account-type separation.
- `IsActive` migration/backfill, explicit server-side provisioning, inactive-account rejection, atomic deactivation revocation and reactivation without credential resurrection.
- Signed `account_type` JWT claim derived from persisted AccountType; missing/malformed/mismatched claim rejection independently of role policy.
- Verified email/mobile password login; verified-mobile OTP login and anti-enumeration.
- OTP purpose, expiry, resend invalidation, single use, attempts, quotas, transactional outbox, provider failover/retry, expiry cleanup and concurrency.
- Staff provisioning authorization, temporary-password flag on create/reset, migration backfill, mandatory post-MFA password change, restricted password-change credential, atomic password update + flag clear + credential consumption + session creation, password reset session revocation, invitation redemption, TOTP enrollment confirmation, recovery-code one-time display, MFA replay prevention, fifth-failure/15-minute lockout and audit.
- DELETE /sessions idempotently revokes all Customer/Staff sessions including current and clears current browser cookies.
- Session creation, lifetime bounds, hash-only refresh persistence and refresh replay/concurrency.
- Cookie flags, CSRF, hostile/missing Origin, exact CORS, role matrix and IDOR.
- Release build, unit tests and PostgreSQL-backed integration tests pass in CI.
- No unresolved critical/high security findings. This does not replace independent production security review.

## Deferred choices

The authoritative register and closure evidence are in [TECHNICAL-DEBT.md](TECHNICAL-DEBT.md). Existing production gates include application authenticated-encryption key custody, JWT signing-key custody, OTP HMAC secret provisioning/rotation, SMS provider/contract, production TLS/origins/cookies, WAF/trusted proxy, distributed rate-limiting decision, backup/DR and privacy retention. Provider selection alone does not close a gate.


## Slice 2C design review resolution

PR #10, the documentation-only Slice 2C authentication ceremony design, was merged into `main` on 2026-11-15. PR #11 subsequently corrected the project progress record. The approved design is maintained in [SLICE-2C-AUTHENTICATION-DESIGN.md](SLICE-2C-AUTHENTICATION-DESIGN.md) and synchronized with SECURITY, PROGRESS, ROADMAP and TECHNICAL-DEBT.

The Foundation implementation contract is now further clarified: add non-null server-controlled `IsActive` (existing Customer/Staff accounts backfilled active; explicit server-side state for new accounts; independent of temporary lockout and `MustChangePassword`), and include a signed `account_type` JWT claim (`customer` or `staff`) derived only from persisted AccountType and enforced separately from role claims.

No authentication endpoint, JWT issuance, login cookie, OTP challenge/outbox worker, Staff MFA enrollment/transaction, TOTP or recovery-code implementation has been added. The current work is still documentation-only; implementation requires its own separately reviewed PR and explicit owner approval.
