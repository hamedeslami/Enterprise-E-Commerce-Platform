# Phase 2 — Identity and Security

**Status:** In progress. This is an implementation progress and navigation document, not a security certification. The detailed security baseline is in [SECURITY.md](SECURITY.md), the session/token contract is in [SESSION-TOKEN-DESIGN.md](SESSION-TOKEN-DESIGN.md), and the authentication ceremony contract is in [SLICE-2C-AUTHENTICATION-DESIGN.md](SLICE-2C-AUTHENTICATION-DESIGN.md).

## Goals

Implement Customer and Staff identity on the API-first Modular Monolith foundation. Use one Identity module and shared session/token infrastructure, with distinct Customer and Staff authentication policies. The browser-facing API uses separate HttpOnly access and refresh cookies with CSRF defenses.

## Delivered foundations

### Identity persistence

The Identity module is registered in the API host and uses ASP.NET Core Identity with a module-owned EF Core context and PostgreSQL identity schema. User and role constructors generate UUIDv7 identifiers; persistence rejects newly added Identity users or roles whose identifiers are not UUIDv7.

Initial Identity password/lockout defaults:
- Minimum password length: 12.
- Digit and lowercase required; uppercase and non-alphanumeric are not mandatory.
- Identity lockout: 5 failed attempts and 15 minutes.
- Identity's global unique normalized-username index remains the Staff username uniqueness boundary. Customer login must not use username as an identifier.

The EF design-time factory, pinned dotnet-ef tool, initial migration, Designer and model snapshot are committed. PostgreSQL-backed integration tests apply the migration and verify the schema.

### Account classification

Every persisted user has an explicit, server-controlled AccountType of Customer or Staff. It is independent of roles. No implicit default or automatic classification is permitted. PostgreSQL constraints reject invalid/unspecified values.

### Session and refresh persistence

PR #9 adds UUIDv7 AuthSession and RefreshToken persistence, database constraints, a partial unique index for one active refresh token per session, and PostgreSQL transaction-based atomic rotation. Rotation uses session-first/token-second locking, constant-time secret verification, database time, conditional token consumption and atomic successor insertion. A valid consumed-token replay revokes only its owning session; wrong-secret attempts do not revoke.

These delivered foundations do not mean that authentication endpoints, access JWT issuance, cookies, CSRF integration, OTP challenges or Staff MFA have been implemented.

## Authentication policy — approved

### Customers

- Password login accepts either email or mobile number through one identifier field.
- Email password login requires EmailConfirmed = true.
- Mobile password login requires PhoneNumberConfirmed = true.
- SMS OTP is available only for an existing Customer with a previously verified mobile number.
- OTP login never creates an account. Registration and verification remain explicit separate flows.
- Customer endpoints reject Staff accounts.

### Staff

- Staff login requires username/password followed by mandatory TOTP.
- A single-use recovery code is the controlled recovery alternative.
- Password success creates only a short-lived MFA transaction; it never creates a normal session or access/refresh credentials.
- SMS OTP is not a Staff factor.
- Staff endpoints reject Customer accounts.

## Slice 2C design review resolution

PR #10 remains documentation-only and open for review. Its revised contract now explicitly defines:
- RFC 6238 TOTP profile: HMAC-SHA-1, six digits, 30-second step, T0=0, ±1 window and atomic per-authenticator replay prevention.
- One PostgreSQL transaction for OTP/MFA/recovery consumption, AuthSession and initial RefreshToken insertion; rollback and concurrent duplicate-submission tests are required.
- Exact Customer-only partial unique indexes on identity.AspNetUsers NormalizedEmail and PhoneNumber for AccountType=Customer, while preserving the global unique normalized-username index; NULL/empty values and AccountType conversion behavior are specified.
- Actor-generic Customer/Staff self-session listing/revocation plus a separately permissioned and audited SecurityAdmin Staff-to-Staff revocation route.
- Recovery codes with at least 128 bits of CSPRNG entropy, one-time Base32 display, domain-separated SHA-256 storage and generic verification failures.
- Lost-response behavior: unreachable sessions are bounded by initial idle/absolute expiry and cleanup; strict refresh replay remains unchanged.
- Exact production and Development cookie names, attributes, clearing behavior and environment separation.

No authentication endpoints, JWT issuance, login cookies, OTP challenge persistence, Staff MFA transaction, TOTP or recovery-code implementation has been added. Passing CI validates repository build/tests only; it is not proof of security correctness.

## Common session and browser policy

- Access JWT maximum lifetime: five minutes.
- Refresh credentials are opaque, cryptographically random, stored only as hashes and rotated atomically.
- Strict refresh replay: a valid consumed-token replay revokes its owning session; no grace period.
- Customer sessions: 30-day absolute / 7-day idle. Staff sessions: 12-hour absolute / 30-minute idle.
- Absolute expiry never slides; idle expiry extends only after successful refresh.
- Access and refresh credentials are separate HttpOnly cookies; never return raw tokens in JSON or place them in browser storage, URLs or logs.
- Enforce antiforgery request-token validation and independent exact-Origin validation for unsafe cookie-authenticated requests.
- Browser topology: https://shop.com, https://panel.shop.com, https://api.shop.com; exact credentialed CORS allow-list and host-only API cookies.

## OTP policy

- SMS delivery channel is confirmed; the vendor remains provider-independent and unselected.
- Six-digit cryptographically random code; two-minute expiry; 60-second resend cooldown; five failed attempts per challenge.
- Five sends per normalized phone per rolling hour and ten per rolling 24 hours.
- HMAC-SHA-256 with a dedicated external secret; challenge ID and purpose included in MAC input.
- Challenge creation, resend invalidation, attempts and consumption must be atomic in PostgreSQL.
- Shared per-phone/global quotas, IP/network limits, risk-based CAPTCHA, provider circuit breaker, bounded concurrency and spend ceiling.

## Implementation sequence

1. Slice 2C design — document and approve contracts before code.
2. Shared session creation and token issuance foundation.
3. Customer email/mobile password and verified-mobile OTP flows.
4. Staff password pre-authentication, TOTP and recovery.
5. Cookie/CSRF/Origin/CORS integration, authorization and session-management endpoints.
6. Security review, concurrency verification and CI.

Each slice is separately reviewable. Do not proceed to the next slice until the current slice's tests and review are complete and the owner approves proceeding. No PR is merged without explicit owner approval.

## Required verification and Phase 2 exit

- Identifier normalization, Customer-only database uniqueness and account-type separation.
- Verified email/mobile password login; verified-mobile OTP login and anti-enumeration.
- OTP purpose, expiry, resend invalidation, single use, attempts, quotas and concurrency.
- Staff password-only denial, TOTP/recovery success, replay prevention, single-use recovery and audit.
- Session creation, lifetime bounds, hash-only refresh persistence and refresh replay/concurrency.
- Cookie flags, CSRF, hostile/missing Origin, exact CORS, role matrix and IDOR.
- Release build, unit tests and PostgreSQL-backed integration tests pass in CI.
- No unresolved critical/high security findings. This does not replace independent production security review.

## Deferred choices

The authoritative register and closure evidence are in [TECHNICAL-DEBT.md](TECHNICAL-DEBT.md). Existing production gates include Data Protection key-ring storage/protection, JWT signing-key custody, OTP HMAC secret provisioning/rotation, SMS provider/contract, production TLS/origins/cookies, WAF/trusted proxy, distributed rate-limiting decision, backup/DR and privacy retention. Provider selection alone does not close a gate.
