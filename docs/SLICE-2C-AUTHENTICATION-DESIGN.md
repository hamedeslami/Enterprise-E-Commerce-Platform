# Slice 2C — Authentication Ceremony Design

**Status:** Proposed implementation contract, based on owner-approved decisions. This document is design-only; it does not claim that authentication endpoints or cryptographic flows are implemented.

## 1. Scope and boundaries

Slice 2C defines how a Customer or Staff account proves identity and transitions into the already-designed session/token lifecycle.

It builds on the merged Identity persistence foundation, explicit `AccountType`, and the merged `AuthSession` / `RefreshToken` persistence and atomic rotation service.

Included:
- Customer password sign-in using either a verified email address or a verified mobile number.
- Customer SMS OTP sign-in using only a previously verified mobile number.
- Staff password sign-in followed by mandatory TOTP MFA or a single-use recovery code.
- Shared initial session creation, access JWT issuance and separate access/refresh HttpOnly cookies.
- Identifier normalization, uniqueness, enumeration resistance, endpoint contracts and security tests.

Not included:
- Customer registration, email confirmation, phone verification, password reset/change, MFA enrollment/reset UI, passkeys/WebAuthn.
- Selection of an Iranian SMS vendor or production KMS/HSM/secret manager.
- Frontend implementation.
- Any claim of production security certification.

No normal authenticated session may be created before the relevant authentication ceremony completes.

## 2. Owner-approved authentication policy

### Customer

A Customer may sign in with:
1. **Email + password**, only when `EmailConfirmed == true`.
2. **Mobile + password**, only when `PhoneNumberConfirmed == true`.
3. **SMS OTP**, only to an existing Customer account whose mobile number is already verified.

The customer password endpoint accepts one `identifier` plus `password`. The server classifies the identifier as email or mobile after normalization; the client does not choose an account type or lookup mode.

Email confirmation and phone confirmation are mandatory for their respective password-login paths. The same phone-verification requirement applies to OTP login. An OTP login must never create/register an account.

### Staff

Staff sign-in is:
1. Username + password.
2. Mandatory TOTP verification, or a single-use recovery code as the controlled recovery alternative.

Password success creates only a short-lived MFA transaction. It MUST NOT create an `AuthSession`, access JWT, refresh token, or normal authentication cookies. SMS OTP is not a Staff factor.

A Staff account cannot authenticate through Customer endpoints; a Customer account cannot authenticate through Staff endpoints. The server derives and enforces `AccountType` from the persisted user record.

## 3. Identifier rules

### Email

- Trim surrounding whitespace and normalize using the configured ASP.NET Core Identity `ILookupNormalizer` / `NormalizeEmail` behavior.
- Do not apply provider-specific transformations such as removing dots or plus-tags.
- Customer email uniqueness is enforced by PostgreSQL on the normalized email for Customer rows; application pre-checks are only for user-friendly errors and are not authoritative.
- Password login resolves only Customer accounts and requires `EmailConfirmed == true`.
- Do not disclose whether an email exists, is unconfirmed, belongs to Staff, or has a wrong password.

### Iranian mobile number

- Accept the explicitly supported input forms `09xxxxxxxxx` and `+989xxxxxxxxx`; normalize Persian/Arabic numerals to ASCII before parsing.
- Store and compare one canonical E.164 representation: `+989xxxxxxxxx`.
- Reject malformed, ambiguous or unsupported formats; do not silently guess country codes.
- Customer mobile uniqueness is enforced by PostgreSQL on the canonical number for Customer rows.
- Password and OTP login both require `PhoneNumberConfirmed == true`.
- Unknown, unverified, Staff-owned and otherwise ineligible numbers receive indistinguishable public OTP-request responses.

### ASP.NET Core Identity compatibility

The existing `ApplicationUser` inherits Identity's `UserName`, `NormalizedUserName`, `Email`, `NormalizedEmail`, `PhoneNumber`, and confirmation flags. The login contract must not accidentally make Customer username a third login identifier.

Implementation must inspect Identity's username validator and unique index behavior before changing persistence. If Identity requires a username for every user, Customer usernames must be server-generated opaque internal values that are never accepted by Customer login endpoints; Staff usernames remain explicitly provisioned and unique. Do not derive a customer username from an email/phone or expose it as a login route.

Database uniqueness requirements:
- Staff usernames remain unique through Identity's normalized-username unique index.
- Customer normalized email is unique among Customer accounts when non-null.
- Customer canonical phone is unique among Customer accounts when non-null.
- These must be database constraints/indexes, not check-then-insert application logic.
- If Staff contact email/phone may overlap a Customer login identifier, the partial Customer-only uniqueness scope must be retained. Account-type conversion must revalidate uniqueness and remain privileged, audited and session-revoking.

## 4. Endpoint contracts

All endpoints are under `/api/v1/auth`. Request DTOs contain no token fields. Access and refresh credentials are returned only through response cookies after successful completion.

| Method and path | Purpose | Session created? |
|---|---|---:|
| POST `/customer/password/login` | Customer email/mobile + password | Yes, on success |
| POST `/customer/otp/request` | Request login OTP for verified Customer mobile | No |
| POST `/customer/otp/verify` | Verify purpose-bound Customer login OTP | Yes, on success |
| POST `/staff/login` | Staff username + password | No |
| POST `/staff/mfa/verify` | Complete Staff TOTP/recovery ceremony | Yes, on success |
| POST `/refresh` | Rotate refresh token and issue new access token | Existing session only |
| POST `/logout` | Revoke current session and clear cookies | Revokes |
| GET `/sessions` | List current Customer's own sessions | No |
| DELETE `/sessions/{sessionId}` | Revoke an owned non-current session | No |

Every unsafe cookie-authenticated request requires the approved CSRF request token and exact trusted Origin validation. The login and OTP-request endpoints are still subject to Origin/CORS policy and endpoint-specific abuse controls; CSRF protection must be applied consistently to cookie-authenticated state changes.

### Customer password request

```json
{
  "identifier": "customer@example.com",
  "password": "..."
}
```

The same `identifier` field accepts a supported mobile format. Do not accept `accountType`, `userId`, role, session lifetime, authentication method or verification flags from the client.

Success: issue the session and cookies; return a minimal authenticated result (for example, user ID and safe account summary) with no raw tokens.

Failure: use a generic authentication failure response for unknown identifier, wrong password, unconfirmed email/mobile, disabled/locked account, or account-type mismatch. Internal telemetry may record a controlled reason code without logging the identifier/password in clear text.

### Customer OTP request

```json
{
  "mobileNumber": "09123456789"
}
```

Use a generic response for all outcomes so the endpoint does not reveal account existence or verification state. Apply phone/IP/global quotas before provider invocation and account lookup where practical. Only a verified Customer mobile may receive a login-purpose OTP.

### Customer OTP verification

```json
{
  "mobileNumber": "09123456789",
  "code": "123456"
}
```

The server resolves the canonical mobile and active login challenge; challenge ID need not be exposed unless a reviewed anti-enumeration design requires it. Verification is purpose-bound, time-bounded, attempt-limited, single-use and atomic in PostgreSQL. Successful verification consumes the challenge and creates exactly one Customer session in the same logical authentication completion flow. Concurrent valid submissions must not create multiple sessions from one challenge.

### Staff password request

```json
{
  "username": "staff.login",
  "password": "..."
}
```

Success does not authenticate the browser. It creates a purpose-bound, single-use, short-lived Staff MFA transaction and sets only the dedicated `__Host-staff-mfa` cookie. The response indicates that MFA is required without disclosing unnecessary account data.

Failure responses are generic. Apply progressive throttling and Identity lockout policy without creating a trivial account-lockout denial-of-service vector.

### Staff MFA verification

TOTP request:

```json
{
  "code": "123456"
}
```

Recovery request:

```json
{
  "recoveryCode": "..."
}
```

The server identifies the pending transaction from the dedicated cookie; never accept a client-supplied user ID or transaction owner. Validate transaction purpose, expiry, cancellation/consumption state and bounded failed attempts. Consume the transaction and (for recovery) the recovery code atomically with session creation. A recovery code is single-use and stored only as a cryptographic hash.

## 5. Session creation and token issuance

Slice 2C must add one shared server-side session-creation boundary used only after a completed authentication ceremony. Do not duplicate session/token creation logic across Customer and Staff endpoints.

Inputs are trusted server-side values:
- Persisted user ID and persisted `AccountType`.
- A server-selected `AuthenticationMethod`.
- Current database-backed time/policy and validated session lifetime options.
- Optional privacy-reviewed device metadata.

Allowed mappings:
- Customer password via email or mobile -> `AuthenticationMethod.Password`.
- Customer SMS OTP -> `AuthenticationMethod.SmsOtp`.
- Staff password + TOTP -> `AuthenticationMethod.PasswordTotp`.
- Staff password + recovery code -> `AuthenticationMethod.PasswordRecoveryCode`.

The session creator must:
1. Re-read or use a transactionally validated user state and ensure the account is enabled, eligible and has the expected AccountType.
2. Load current server-side roles; never copy roles or assurance from request/JWT/client state.
3. Select Customer (30-day absolute / 7-day idle) or Staff (12-hour absolute / 30-minute idle) policy from validated server configuration.
4. Use UTC/database time and enforce `IdleExpiresAt <= AbsoluteExpiresAt`.
5. Create one UUIDv7 `AuthSession` and exactly one initial opaque 256-bit refresh token in a transaction. Persist only SHA-256(refresh secret), never the raw secret.
6. Mint a short-lived RS256 access JWT (maximum five minutes) with the approved minimal claims and server-issued role claims.
7. Set separate host-only HttpOnly cookies on `api.shop.com`; use Secure in production, SameSite=Lax, Path=/, explicit expiry and no Domain attribute. Prefer `__Host-` names where their cookie-prefix requirements are met.
8. Return no raw access/refresh token in JSON, logs, traces or exceptions.

If session persistence succeeds but signing/cookie preparation fails, the implementation must not leave an unexpectedly usable orphan credential. Define transaction/compensation boundaries so session and initial refresh-token creation are atomic; do not send cookies before all required server-side work succeeds. A failed response must not disclose credentials.

Production key custody remains governed by TD-SEC-002; production Data Protection remains governed by TD-SEC-001. Local development may use isolated development keys, but production must fail closed without approved key material.

## 6. Staff MFA transaction and TOTP requirements

- MFA transaction lifetime: maximum five minutes, server-configured but never greater than five minutes.
- Pre-authentication secret: cryptographically random, opaque and high entropy; persist only a hash.
- Cookie: `__Host-staff-mfa`, Secure, HttpOnly, SameSite=Lax, Path=/, no Domain, maximum five-minute lifetime.
- Transaction is bound to one Staff user and one fixed purpose; it cannot be upgraded into a normal session except by successful MFA completion.
- Bound failed attempts; consume/cancel/expire atomically. Password success must not reset or bypass MFA attempt policy.
- TOTP secret is encrypted at rest using the approved Data Protection boundary or a reviewed equivalent; it is never returned after enrollment and never logged.
- TOTP enrollment is not active until a generated code is verified.
- Validate TOTP with a small, explicit clock-skew window and prevent replay of an already accepted time-step for the same authenticator. Persist the last accepted time-step/counter atomically to prevent concurrent reuse.
- Recovery codes are generated with a cryptographically secure RNG, displayed once, stored only as hashes, single-use and consumed atomically.
- MFA reset/recovery and recovery-code use are audited. No support/admin override may silently bypass MFA.
- SMS is not an acceptable Staff second factor.

## 7. OTP security and abuse controls

Retain the approved baseline:
- Six-digit code from a cryptographically secure RNG.
- Two-minute challenge lifetime.
- Sixty-second resend cooldown.
- Maximum five failed attempts per challenge.
- Maximum five sends per normalized phone per rolling hour and ten per rolling 24 hours.
- Dedicated HMAC-SHA-256 secret outside the database; MAC input includes challenge ID and purpose. Compare in constant time.
- Challenge creation, resend invalidation, attempt increments and successful consumption are atomic PostgreSQL operations.
- Shared per-phone/global quotas in PostgreSQL; IP/network controls at API and edge; risk-triggered CAPTCHA is supplementary.
- Provider concurrency/spend budget, circuit breaker and bounded retries.
- Generic public responses; never log raw OTP, full phone, credentials, cookies or tokens.

OTP challenge purpose for this slice is specifically Customer login. Registration/phone-verification purposes are separate flows and must not be accepted by the login verifier.

## 8. Failure, concurrency and authorization invariants

- Customer and Staff endpoints enforce AccountType server-side even when an identifier happens to match another account's data.
- Customer email/mobile lookup never falls back to Staff accounts.
- Staff username lookup never falls back to Customer accounts.
- Unconfirmed email/mobile cannot authenticate by password; unverified mobile cannot receive or verify login OTP.
- OTP verification and Staff MFA completion are single-use under concurrency.
- A successful authentication ceremony creates one session and one initial active refresh token; duplicate submissions cannot create multiple sessions from one one-time challenge/transaction.
- Session creation and refresh rotation use consistent session/token lock ordering from the existing session-token design. Every operation that mutates both session and refresh-token state locks session first.
- Current roles are loaded from server-side Identity data at issuance; no client role or account classification is trusted.
- Customer session list/revoke operations are ownership-scoped and cannot expose/revoke another customer's sessions.
- Staff endpoints and staff sessions require Staff AccountType, appropriate role/policy and completed MFA.
- Access JWT revocation remains bounded by its approved maximum lifetime plus clock skew; do not claim immediate stateless JWT invalidation.

## 9. Required verification

### Identifier and account separation
- Email normalization, case-insensitive lookup and duplicate normalized Customer email rejection at PostgreSQL level.
- Iranian mobile numeral normalization and accepted/rejected input formats.
- Canonical phone uniqueness for Customer accounts, including concurrent registration/update attempts.
- Customer endpoint rejects Staff even if email/mobile matches; Staff endpoint rejects Customer even if username matches.
- Customer username is never treated as a login identifier.
- Email login rejects unconfirmed email; mobile password login rejects unconfirmed phone; OTP request/verify rejects unverified phone.

### Customer password and OTP
- Correct/incorrect password, disabled/locked account, unknown identifier and unconfirmed identifier.
- Generic failure response and no account enumeration.
- OTP purpose binding, expiry, resend invalidation, single use, max attempts, wrong-code behavior and concurrent verification.
- Quotas under concurrent requests and multiple API instances; provider timeout/failure/circuit breaker and no unbounded retry.
- Raw password, OTP, phone and token values absent from logs/traces.

### Staff MFA
- Password-only Staff login creates no AuthSession/access/refresh cookie.
- Customer cannot enter Staff MFA flow.
- TOTP success, invalid code, expired transaction, replayed time-step, concurrent duplicate code and attempt exhaustion.
- Recovery code success, reuse rejection, concurrent consumption and audit event.
- TOTP secret encryption at rest and no secret disclosure.
- Staff session is created only after MFA success.

### Session, token, browser and authorization
- Correct Customer/Staff lifetime selection and absolute-expiry cap.
- Exactly one initial refresh token per new session; hash-only persistence.
- RS256, key ID, issuer/audience/lifetime validation and five-minute maximum.
- Cookie attributes, host-only scope, no Domain, CSRF token, missing/hostile Origin and exact CORS allow-list.
- Refresh concurrency/replay, logout and session revocation regression tests.
- Role matrix, horizontal/vertical escalation, IDOR and session ownership.
- Build, unit tests and PostgreSQL-backed integration tests pass in CI.

## 10. Implementation sequence and gates

1. **2C-Design (this document):** approve endpoint, normalization, persistence and ceremony contracts before implementation.
2. **2C-Foundation:** shared validated session policy and session-creation service; initial refresh token + access-token issuance boundary; unit/PostgreSQL tests.
3. **2C-Customer:** email/mobile password login and verified-mobile OTP request/verify; uniqueness and anti-enumeration tests.
4. **2C-Staff:** password pre-authentication transaction, TOTP verification, replay prevention and recovery-code consumption.
5. **2C-Completion:** cookie/CSRF/Origin/CORS integration, authorization policies, session management endpoints and end-to-end tests.
6. **2C-Security review:** inspect full diff, threat model, concurrency coverage, CI and debt register; no merge without explicit owner approval.

Each implementation slice is a separate reviewable PR or an explicitly agreed small set of PRs. Every PR updates progress and any technical-debt rows it introduces. Do not begin the next slice until the current slice's tests and review are complete and the owner approves proceeding.

## 11. Explicit non-goals and tracked production gates

No new deferral is created merely by documenting a provider-independent boundary. Existing gates remain authoritative:
- TD-SEC-001: production Data Protection key-ring storage/protection.
- TD-SEC-002: JWT signing-key custody and runtime integration.
- TD-SEC-003: OTP HMAC secret provisioning/rotation.
- TD-SEC-004: SMS provider/sender/contract.
- TD-SEC-005: production origins/TLS/cookie configuration.
- TD-SEC-006: WAF/CDN/DDoS/trusted proxy.
- TD-SEC-007: distributed rate limiting/Redis decision.
- TD-OPS-002: privacy retention/deletion.

Provider selection alone does not close any gate. Production must remain fail-closed where the security design requires it.
