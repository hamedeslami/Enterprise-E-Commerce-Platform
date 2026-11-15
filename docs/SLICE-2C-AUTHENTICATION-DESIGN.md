# Slice 2C — Authentication Ceremony Design

**Status:** Proposed implementation contract, updated after the design review. This document is design-only; no authentication endpoint, credential issuance flow or cryptographic ceremony is implemented by this PR.

## 1. Scope and boundaries

Slice 2C defines how Customer and Staff accounts authenticate and transition into the merged session/token lifecycle.

Included:
- Customer password sign-in using a verified email or verified Iranian mobile.
- Customer SMS OTP sign-in for an existing Customer with a previously verified mobile.
- Staff password sign-in followed by mandatory TOTP or a single-use recovery code.
- One shared session-creation boundary, access JWT issuance and separate access/refresh cookies.
- Identifier normalization, database uniqueness, endpoint contracts, abuse controls and security verification.

Not included: registration, email confirmation, phone verification, password reset/change, MFA enrollment/reset UI, passkeys/WebAuthn, frontend work, SMS-vendor selection or production KMS/HSM selection. This document is not a production security certification.

No normal authenticated session may exist before the relevant authentication ceremony completes.

## 2. Owner-approved authentication policy

### Customer

A Customer may authenticate using:
1. Email + password, only when EmailConfirmed is true.
2. Mobile + password, only when PhoneNumberConfirmed is true.
3. SMS OTP, only for an existing Customer account whose mobile is already verified.

The password endpoint accepts one identifier and password. The server normalizes and classifies the identifier; the client cannot choose account type or lookup mode. OTP login never creates an account.

### Staff

Staff authentication is username + password followed by mandatory TOTP. A single-use recovery code is the controlled recovery alternative. SMS OTP is never a Staff factor.

Password success creates only a short-lived, purpose-bound MFA transaction. It MUST NOT create an AuthSession, access JWT, refresh token or normal authentication cookie.

Every endpoint enforces the persisted AccountType. Customer endpoints never fall back to Staff records; Staff endpoints never fall back to Customer records.

## 3. Identifier and database uniqueness contract

### Email

- Trim surrounding whitespace and normalize using the configured ASP.NET Core Identity ILookupNormalizer / NormalizeEmail behavior.
- Do not remove provider-specific dots or plus-tags.
- Customer email uniqueness is enforced by PostgreSQL on NormalizedEmail for Customer rows. Application pre-checks are only for user-friendly errors.
- Email password login requires EmailConfirmed = true.
- Public responses do not disclose existence, confirmation state, account type, lockout or password correctness.

### Iranian mobile

- Accept only 09xxxxxxxxx and +989xxxxxxxxx.
- Convert Persian and Arabic numerals to ASCII before parsing.
- Store PhoneNumber in canonical E.164 form: +989xxxxxxxxx.
- Reject malformed, ambiguous and unsupported formats; do not guess country codes.
- Customer mobile uniqueness is enforced by PostgreSQL on canonical PhoneNumber for Customer rows.
- Password and OTP login require PhoneNumberConfirmed = true.
- Unknown, unverified, Staff-owned and otherwise ineligible numbers receive indistinguishable public OTP-request responses.

### Identity compatibility and exact index scope

The existing Identity table is identity.AspNetUsers. Preserve the global unique UserNameIndex on NormalizedUserName; it remains the Staff username uniqueness boundary. Customer username is never a login identifier. If Identity requires a username, generate an opaque server-side value that is not derived from email/mobile and is never accepted by Customer login endpoints.

The current Identity model has a non-unique EmailIndex on NormalizedEmail. That index is not a uniqueness guarantee. Keep it unless a reviewed migration replaces it with an equivalent lookup index, and add these unique partial indexes:

- Customer email: unique index on identity.AspNetUsers (NormalizedEmail) with predicate AccountType = 1 AND NormalizedEmail IS NOT NULL.
- Customer mobile: unique index on identity.AspNetUsers (PhoneNumber) with predicate AccountType = 1 AND PhoneNumber IS NOT NULL.

AccountType value 1 is Customer, as defined by the persisted enum. Empty or whitespace email/mobile values are not valid “absent” values: normalize absence to SQL NULL before persistence. Add these database checks (using the existing identity.AspNetUsers column names): CHECK (NormalizedEmail IS NULL OR (NormalizedEmail <> '' AND NormalizedEmail = btrim(NormalizedEmail))); CHECK (PhoneNumber IS NULL OR PhoneNumber ~ '^\+989[0-9]{9}$').

The unique global normalized-username index remains in force for both account types. Staff contact email/phone may overlap Customer login identifiers because the Customer partial indexes intentionally scope only AccountType = 1.

Changing AccountType is a privileged, audited operation in one PostgreSQL transaction. The update must satisfy all destination-type uniqueness constraints; any collision aborts the entire transaction. Successful account-type change revokes all sessions in the same transaction. No application check-then-write is authoritative.

## 4. Endpoint contracts

All routes are under /api/v1/auth. DTOs contain no token fields. Credentials are returned only through response cookies after successful completion.

| Method and path | Purpose | Session created? |
|---|---|---:|
| POST /customer/password/login | Customer email/mobile + password | Yes, on success |
| POST /customer/otp/request | Request login OTP for verified Customer mobile | No |
| POST /customer/otp/verify | Verify purpose-bound Customer login OTP | Yes, on success |
| POST /staff/login | Staff username + password | No |
| POST /staff/mfa/verify | Complete Staff TOTP/recovery ceremony | Yes, on success |
| POST /refresh | Rotate refresh token and issue access token | Existing session |
| POST /logout | Revoke current session and clear cookies | Revokes |
| GET /sessions | List the current actor's own sessions | No |
| DELETE /sessions/{sessionId} | Revoke an owned non-current session | No |
| DELETE /staff/{staffUserId}/sessions/{sessionId} | Security-admin revocation of a Staff session | No |

The self-session routes are actor-generic: the authenticated Customer or Staff actor can list only their own sessions and revoke only their own non-current sessions. The response identifies the current session. Neither actor can revoke the current session through the single-session route; use logout or the explicit sign-out-all-devices action for that. Customer sign-out-all revokes all of that Customer's sessions, including current. Staff self-service sign-out-all follows the same rule.

The staff-to-staff route requires a dedicated SecurityAdmin permission, Staff AccountType and completed MFA. It verifies that the target session belongs to the target Staff user, prevents cross-account IDOR, records actor/target/session/reason in the audit log and performs revocation atomically. It does not permit a Customer actor or ordinary Staff role to revoke another Staff member's sessions.

Every unsafe cookie-authenticated request requires the approved antiforgery request token and exact trusted Origin validation. Login and OTP-request endpoints remain subject to exact Origin/CORS policy and endpoint-specific abuse controls.

### Customer password

Request contains identifier and password. The identifier may be email or one of the supported mobile formats. Do not accept accountType, userId, role, session lifetime, authentication method or verification flags from the client.

Success creates a session and sets cookies; JSON contains only a minimal safe account result and no raw token. Failure is generic for unknown identifier, wrong password, unconfirmed identifier, disabled/locked account or account-type mismatch.

### Customer OTP

OTP request accepts mobileNumber. Its public response is generic for every outcome. Apply phone/IP/global quotas and risk controls; only a verified Customer mobile receives a login-purpose challenge.

OTP verification accepts mobileNumber and code. The server resolves the canonical mobile and active Customer-login challenge. The challenge is purpose-bound, time-bounded, attempt-limited, single-use and atomic. Concurrent submissions cannot create multiple sessions from one challenge.

### Staff password and MFA

Staff login accepts username and password. Success creates a single-use MFA transaction and sets only the dedicated MFA cookie. It does not authenticate the browser or issue normal session credentials.

MFA verification accepts either a TOTP code or a recovery code. The server resolves the pending transaction from the dedicated cookie; never accept client-supplied user ID or transaction owner. Validate purpose, expiry, cancellation/consumption state and bounded failed attempts.

## 5. Shared session creation and transaction boundary

All completed ceremonies use one server-side session-creation service. It accepts only trusted server-side user ID, persisted AccountType, server-selected AuthenticationMethod, validated lifetime policy, database time and privacy-reviewed device metadata.

Authentication-method mapping:
- Customer password via email or mobile -> Password.
- Customer SMS OTP -> SmsOtp.
- Staff password + TOTP -> PasswordTotp.
- Staff password + recovery code -> PasswordRecoveryCode.

### Required atomicity

For Customer OTP and Staff MFA completion, the following state changes MUST commit or roll back together in one PostgreSQL transaction using the same IdentityDbContext/connection:

1. Lock and revalidate the OTP challenge or Staff MFA transaction.
2. For Staff TOTP, atomically advance the authenticator's last accepted time-step; for recovery, atomically consume the selected recovery code.
3. Consume the OTP challenge or Staff MFA transaction.
4. Revalidate account enabled state, expected AccountType and current authorization state.
5. Insert exactly one AuthSession.
6. Insert exactly one initial opaque 256-bit RefreshToken, persisting only SHA-256(secret).

No one-time factor may be consumed without the session and initial refresh token being committed. No session may be committed while the factor remains reusable. Password-based Customer session creation uses the same session/refresh transaction but has no one-time challenge to consume.

Generate session identifiers, refresh secret and token claims from server-side state. Access JWT signing must complete before the database transaction commits so signing failure rolls back factor consumption and session persistence. Signing in this transaction must be a bounded local signing operation; do not perform an unbounded remote network call while holding database locks. Cookie serialization/options must be validated before commit. Cookies are appended only after a successful commit. If commit fails, no credentials are returned.

If the database commit succeeds but the HTTP response is lost before the browser receives cookies, the resulting session is unreachable to that browser. Do not weaken strict refresh replay or add a retry grace period. The orphan remains bounded by the session's initial idle expiry (Customer: at most seven days; Staff: at most thirty minutes) and absolute expiry; a cleanup job must revoke/expire sessions that have passed idle/absolute expiry and remove expired refresh credentials according to retention policy. The user can authenticate again, creating a separate session. Do not attempt to infer successful cookie delivery from server-side response completion.

### Session policy

- Customer: 30-day absolute / 7-day idle.
- Staff: 12-hour absolute / 30-minute idle.
- Absolute expiry never slides; idle expiry extends only after successful refresh.
- Use UTC/database time and enforce IdleExpiresAt <= AbsoluteExpiresAt.
- Load current roles from server-side Identity data; never copy roles or assurance from client/JWT state.
- Access JWT is RS256, maximum five minutes, with approved minimal claims and server-issued role claims.
- Session and initial refresh-token insertion are atomic. Persist only SHA-256(refresh secret), never the raw secret.
- No raw access/refresh token in JSON, logs, traces or exceptions.

## 6. Staff MFA, TOTP and recovery codes

### TOTP profile

Use RFC 6238 with this fixed initial interoperability profile:
- HMAC-SHA-1.
- Six decimal digits.
- 30-second time-step.
- T0 = 0 (Unix epoch).
- Accept at most one step in the past and one step in the future (±1); do not widen dynamically.
- Use a cryptographically secure random TOTP secret; encrypt it at rest using the approved Data Protection boundary or a reviewed equivalent. Never return it after enrollment or log it.
- Enrollment remains pending until the first generated code is successfully verified.

Persist LastAcceptedTimeStep per authenticator. A submitted code is valid only if its matching step is greater than LastAcceptedTimeStep. If more than one candidate step matches, select the greatest matching step that is still within the allowed window and greater than the persisted value. Advance the value with a conditional database update or row lock in the same transaction as MFA transaction consumption and session creation. A concurrent duplicate submission must have at most one winner. A code from an already accepted step is rejected even while it remains inside the clock-skew window.

If a future implementation changes algorithm/digits/period, it is a new reviewed interoperability decision and must update authenticator enrollment metadata and URI parameters; do not silently change this profile.

### MFA transaction

- Lifetime is server-configured but never greater than five minutes.
- Pre-authentication secret is cryptographically random, opaque and high entropy; persist only a hash.
- Cookie is __Host-staff-mfa; production attributes are Secure, HttpOnly, SameSite=Lax, Path=/, no Domain, maximum five-minute lifetime.
- Transaction is bound to one Staff user and one fixed purpose.
- It can only complete/cancel that transaction; it cannot be upgraded without successful MFA.
- Failed attempts are bounded. Consume/cancel/expire atomically.
- Password success does not reset or bypass MFA attempt policy.

### Recovery codes

- Generate each code from at least 128 bits of CSPRNG entropy (16 random bytes).
- Encode as unpadded RFC 4648 Base32 using uppercase A-Z and 2-7; display once only. UI may group characters with hyphens for readability.
- Normalize by uppercasing and removing only the documented grouping hyphens; reject any other character or malformed length.
- Persist only SHA-256 over the exact UTF-8 byte sequence: ASCII label 'staff-recovery-v1', one 0x00 byte, the owning user UUID in lowercase 32-character N format, one 0x00 byte, then the normalized Base32 code. No plaintext or reversible encryption is stored. Given at least 128 bits of entropy, a password KDF is not required for offline guessing resistance; retain constant-time verification/consumption behavior.
- A recovery code is consumed atomically with MFA transaction consumption and session/refresh creation. It is one-time and cannot be reused concurrently.
- Display each generated code once; never return it from later APIs or log it.
- All invalid, unknown, used and wrong-owner codes produce the same public failure. Do not expose match/no-match details or provide a code-enumeration oracle.
- Recovery-code generation, use, reset and administrative recovery are audited.

SMS is not an acceptable Staff second factor. No support/admin override may silently bypass MFA.

## 7. Cookie names, attributes and clearing behavior

Production cookies are issued by api.shop.com as host-only cookies. No Domain attribute is ever set.

| Purpose | Exact production cookie name |
|---|---|
| Access JWT | __Host-access-token |
| Refresh token | __Host-refresh-token |
| Staff MFA transaction | __Host-staff-mfa |

All three production cookies use Secure, HttpOnly, SameSite=Lax and Path=/. Their expiry is explicit and never exceeds the underlying credential/transaction expiry. The __Host- prefix is valid because Secure is set, Path is exactly /, and Domain is omitted. Access and refresh remain separate cookies.

Development over local HTTP uses separate names: dev-access-token, dev-refresh-token and dev-staff-mfa. These names are permitted only in the Development environment and only for the local HTTP origin. Secure=false is allowed only there. Production/Staging must reject development cookie configuration and must never emit dev-prefixed cookies. Production must never fall back to non-Secure cookies.

Cookie lifecycle:
- Successful Customer password or OTP login: set access + refresh; clear any stale staff-MFA cookie.
- Successful Staff MFA: set access + refresh; clear staff-MFA cookie.
- Successful Staff password pre-authentication: set/replace only staff-MFA; do not set or clear normal session cookies.
- Failed password login or failed OTP request/verification: do not clear an unrelated existing authenticated session cookie. Keep the MFA cookie only while its transaction remains valid and attempts remain.
- Expired, consumed, cancelled or attempt-exhausted MFA transaction: clear staff-MFA cookie.
- Refresh success: rotate refresh and replace access cookie; refresh failure due to invalid/expired/revoked/replayed credentials clears access and refresh cookies.
- Logout: revoke current session and clear access, refresh and staff-MFA cookies.
- Sign-out-all-devices clears current browser's access/refresh cookies after revoking all applicable sessions.
- Cookie deletion must use the same name, Path and host-only scope as issuance, with an expired value/Max-Age=0; never add Domain during deletion.

## 8. OTP security and abuse controls

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

OTP purpose in this slice is specifically Customer login. Registration/phone-verification purposes are separate flows and must not be accepted by the login verifier.

## 9. Failure, concurrency and authorization invariants

- Customer and Staff endpoints enforce AccountType server-side.
- Customer email/mobile lookup never falls back to Staff accounts; Staff username lookup never falls back to Customer accounts.
- Unconfirmed email/mobile cannot authenticate by password; unverified mobile cannot receive or verify login OTP.
- OTP verification and Staff MFA completion are single-use under concurrency.
- Challenge/MFA consumption, recovery-code consumption where applicable, AuthSession insertion and initial RefreshToken insertion are atomic.
- A successful ceremony creates exactly one session and one initial active refresh token.
- Session creation and refresh rotation use consistent session/token lock ordering: session first, then token.
- Self-session operations are ownership-scoped for both Customer and Staff. Security-admin Staff-to-Staff revocation is separately permissioned and audited.
- Current roles are loaded from server-side Identity data; no client role/account classification is trusted.
- Staff endpoints and Staff sessions require Staff AccountType, the required role/policy and completed MFA.
- Stateless access JWT revocation remains bounded by its approved maximum lifetime plus clock skew.

## 10. Required verification

### Identifier and account separation
- Email normalization, case-insensitive lookup and duplicate normalized Customer email rejection at PostgreSQL level.
- Iranian mobile numeral normalization and accepted/rejected input formats.
- Canonical phone uniqueness for Customer accounts, including concurrent writes.
- Exact partial-index predicates, NULL/empty-value checks, and migration failure on existing duplicates.
- AccountType conversion collision rolls back both conversion and session revocation; successful conversion revokes all sessions atomically.
- Customer rejects Staff and Staff rejects Customer; Customer username is never a login identifier.

### Customer password and OTP
- Correct/incorrect password, disabled/locked account, unknown identifier and unconfirmed identifier.
- Generic failure response and no account enumeration.
- OTP purpose binding, expiry, resend invalidation, single use, max attempts, wrong-code behavior and concurrent verification.
- OTP challenge consumption + session + initial refresh insertion rollback together on forced failure.
- Concurrent duplicate OTP verification creates at most one session.
- Quotas under concurrent requests and multiple API instances; provider timeout/failure/circuit breaker and no unbounded retry.
- Raw password, OTP, phone and token values absent from logs/traces.

### Staff MFA
- Password-only Staff login creates no AuthSession/access/refresh cookie.
- Customer cannot enter Staff MFA flow.
- TOTP RFC profile test vectors, past/current/future window boundaries, stale step rejection and concurrent duplicate-step rejection.
- MFA transaction consumption + TOTP step advance + session + initial refresh insertion roll back together on forced failure.
- Recovery-code entropy/encoding, one-time display, normalization, wrong-owner rejection, reuse rejection and concurrent consumption.
- Recovery-code consumption + MFA transaction + session + initial refresh insertion roll back together on forced failure.
- Generic failures and no recovery-code enumeration.
- TOTP secret encryption at rest and no secret disclosure.
- Staff session is created only after MFA success.

### Session, cookie, browser and authorization
- Correct Customer/Staff lifetime selection and absolute-expiry cap.
- Exactly one initial refresh token per new session; hash-only persistence.
- RS256, key ID, issuer/audience/lifetime validation and five-minute maximum.
- Production and Development cookie names/attributes; production never emits dev names or insecure cookies.
- Cookie clearing behavior on refresh failure, logout, MFA completion and terminal MFA failure.
- Lost-response orphan session remains bounded by initial idle expiry and cleanup.
- CSRF token, missing/hostile Origin and exact CORS allow-list.
- Refresh concurrency/replay, logout and session revocation regression tests.
- Customer and Staff self-session ownership, current-session protection, security-admin permission and audit coverage.
- Role matrix, horizontal/vertical escalation and IDOR.
- Build, unit tests and PostgreSQL-backed integration tests pass in CI.

## 11. Implementation sequence and gates

1. **2C-Design (this document):** approve endpoint, normalization, persistence and ceremony contracts before implementation.
2. **2C-Foundation:** shared validated session policy and session-creation service; initial refresh token + access-token issuance boundary; unit/PostgreSQL tests.
3. **2C-Customer:** email/mobile password login and verified-mobile OTP request/verify; uniqueness and anti-enumeration tests.
4. **2C-Staff:** password pre-authentication transaction, TOTP verification, replay prevention and recovery-code consumption.
5. **2C-Completion:** cookie/CSRF/Origin/CORS integration, authorization policies, Customer/Staff session management endpoints and end-to-end tests.
6. **2C-Security review:** inspect full diff, threat model, concurrency coverage, CI and debt register; no merge without explicit owner approval.

Each implementation slice is a separate reviewable PR or an explicitly agreed small set of PRs. Every PR updates progress and any technical-debt rows it introduces. Do not begin the next slice until the current slice's tests and review are complete and the owner approves proceeding.

## 12. Explicit non-goals and tracked production gates

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
). The application must keep Email and NormalizedEmail nullability aligned; the mobile check enforces the canonical Iranian E.164 shape at the database boundary. These checks make the partial-index predicates exact and avoid empty values being treated as identifiers. Existing rows must be audited and normalized before creating the indexes; migration must fail atomically on conflicting Customer identifiers rather than silently choosing a winner.

The unique global normalized-username index remains in force for both account types. Staff contact email/phone may overlap Customer login identifiers because the Customer partial indexes intentionally scope only AccountType = 1.

Changing AccountType is a privileged, audited operation in one PostgreSQL transaction. The update must satisfy all destination-type uniqueness constraints; any collision aborts the entire transaction. Successful account-type change revokes all sessions in the same transaction. No application check-then-write is authoritative.

## 4. Endpoint contracts

All routes are under /api/v1/auth. DTOs contain no token fields. Credentials are returned only through response cookies after successful completion.

| Method and path | Purpose | Session created? |
|---|---|---:|
| POST /customer/password/login | Customer email/mobile + password | Yes, on success |
| POST /customer/otp/request | Request login OTP for verified Customer mobile | No |
| POST /customer/otp/verify | Verify purpose-bound Customer login OTP | Yes, on success |
| POST /staff/login | Staff username + password | No |
| POST /staff/mfa/verify | Complete Staff TOTP/recovery ceremony | Yes, on success |
| POST /refresh | Rotate refresh token and issue access token | Existing session |
| POST /logout | Revoke current session and clear cookies | Revokes |
| GET /sessions | List the current actor's own sessions | No |
| DELETE /sessions/{sessionId} | Revoke an owned non-current session | No |
| DELETE /staff/{staffUserId}/sessions/{sessionId} | Security-admin revocation of a Staff session | No |

The self-session routes are actor-generic: the authenticated Customer or Staff actor can list only their own sessions and revoke only their own non-current sessions. The response identifies the current session. Neither actor can revoke the current session through the single-session route; use logout or the explicit sign-out-all-devices action for that. Customer sign-out-all revokes all of that Customer's sessions, including current. Staff self-service sign-out-all follows the same rule.

The staff-to-staff route requires a dedicated SecurityAdmin permission, Staff AccountType and completed MFA. It verifies that the target session belongs to the target Staff user, prevents cross-account IDOR, records actor/target/session/reason in the audit log and performs revocation atomically. It does not permit a Customer actor or ordinary Staff role to revoke another Staff member's sessions.

Every unsafe cookie-authenticated request requires the approved antiforgery request token and exact trusted Origin validation. Login and OTP-request endpoints remain subject to exact Origin/CORS policy and endpoint-specific abuse controls.

### Customer password

Request contains identifier and password. The identifier may be email or one of the supported mobile formats. Do not accept accountType, userId, role, session lifetime, authentication method or verification flags from the client.

Success creates a session and sets cookies; JSON contains only a minimal safe account result and no raw token. Failure is generic for unknown identifier, wrong password, unconfirmed identifier, disabled/locked account or account-type mismatch.

### Customer OTP

OTP request accepts mobileNumber. Its public response is generic for every outcome. Apply phone/IP/global quotas and risk controls; only a verified Customer mobile receives a login-purpose challenge.

OTP verification accepts mobileNumber and code. The server resolves the canonical mobile and active Customer-login challenge. The challenge is purpose-bound, time-bounded, attempt-limited, single-use and atomic. Concurrent submissions cannot create multiple sessions from one challenge.

### Staff password and MFA

Staff login accepts username and password. Success creates a single-use MFA transaction and sets only the dedicated MFA cookie. It does not authenticate the browser or issue normal session credentials.

MFA verification accepts either a TOTP code or a recovery code. The server resolves the pending transaction from the dedicated cookie; never accept client-supplied user ID or transaction owner. Validate purpose, expiry, cancellation/consumption state and bounded failed attempts.

## 5. Shared session creation and transaction boundary

All completed ceremonies use one server-side session-creation service. It accepts only trusted server-side user ID, persisted AccountType, server-selected AuthenticationMethod, validated lifetime policy, database time and privacy-reviewed device metadata.

Authentication-method mapping:
- Customer password via email or mobile -> Password.
- Customer SMS OTP -> SmsOtp.
- Staff password + TOTP -> PasswordTotp.
- Staff password + recovery code -> PasswordRecoveryCode.

### Required atomicity

For Customer OTP and Staff MFA completion, the following state changes MUST commit or roll back together in one PostgreSQL transaction using the same IdentityDbContext/connection:

1. Lock and revalidate the OTP challenge or Staff MFA transaction.
2. For Staff TOTP, atomically advance the authenticator's last accepted time-step; for recovery, atomically consume the selected recovery code.
3. Consume the OTP challenge or Staff MFA transaction.
4. Revalidate account enabled state, expected AccountType and current authorization state.
5. Insert exactly one AuthSession.
6. Insert exactly one initial opaque 256-bit RefreshToken, persisting only SHA-256(secret).

No one-time factor may be consumed without the session and initial refresh token being committed. No session may be committed while the factor remains reusable. Password-based Customer session creation uses the same session/refresh transaction but has no one-time challenge to consume.

Generate session identifiers, refresh secret and token claims from server-side state. Access JWT signing must complete before the database transaction commits so signing failure rolls back factor consumption and session persistence. Signing in this transaction must be a bounded local signing operation; do not perform an unbounded remote network call while holding database locks. Cookie serialization/options must be validated before commit. Cookies are appended only after a successful commit. If commit fails, no credentials are returned.

If the database commit succeeds but the HTTP response is lost before the browser receives cookies, the resulting session is unreachable to that browser. Do not weaken strict refresh replay or add a retry grace period. The orphan remains bounded by the session's initial idle expiry (Customer: at most seven days; Staff: at most thirty minutes) and absolute expiry; a cleanup job must revoke/expire sessions that have passed idle/absolute expiry and remove expired refresh credentials according to retention policy. The user can authenticate again, creating a separate session. Do not attempt to infer successful cookie delivery from server-side response completion.

### Session policy

- Customer: 30-day absolute / 7-day idle.
- Staff: 12-hour absolute / 30-minute idle.
- Absolute expiry never slides; idle expiry extends only after successful refresh.
- Use UTC/database time and enforce IdleExpiresAt <= AbsoluteExpiresAt.
- Load current roles from server-side Identity data; never copy roles or assurance from client/JWT state.
- Access JWT is RS256, maximum five minutes, with approved minimal claims and server-issued role claims.
- Session and initial refresh-token insertion are atomic. Persist only SHA-256(refresh secret), never the raw secret.
- No raw access/refresh token in JSON, logs, traces or exceptions.

## 6. Staff MFA, TOTP and recovery codes

### TOTP profile

Use RFC 6238 with this fixed initial interoperability profile:
- HMAC-SHA-1.
- Six decimal digits.
- 30-second time-step.
- T0 = 0 (Unix epoch).
- Accept at most one step in the past and one step in the future (±1); do not widen dynamically.
- Use a cryptographically secure random TOTP secret; encrypt it at rest using the approved Data Protection boundary or a reviewed equivalent. Never return it after enrollment or log it.
- Enrollment remains pending until the first generated code is successfully verified.

Persist LastAcceptedTimeStep per authenticator. A submitted code is valid only if its matching step is greater than LastAcceptedTimeStep. If more than one candidate step matches, select the greatest matching step that is still within the allowed window and greater than the persisted value. Advance the value with a conditional database update or row lock in the same transaction as MFA transaction consumption and session creation. A concurrent duplicate submission must have at most one winner. A code from an already accepted step is rejected even while it remains inside the clock-skew window.

If a future implementation changes algorithm/digits/period, it is a new reviewed interoperability decision and must update authenticator enrollment metadata and URI parameters; do not silently change this profile.

### MFA transaction

- Lifetime is server-configured but never greater than five minutes.
- Pre-authentication secret is cryptographically random, opaque and high entropy; persist only a hash.
- Cookie is __Host-staff-mfa; production attributes are Secure, HttpOnly, SameSite=Lax, Path=/, no Domain, maximum five-minute lifetime.
- Transaction is bound to one Staff user and one fixed purpose.
- It can only complete/cancel that transaction; it cannot be upgraded without successful MFA.
- Failed attempts are bounded. Consume/cancel/expire atomically.
- Password success does not reset or bypass MFA attempt policy.

### Recovery codes

- Generate each code from at least 128 bits of CSPRNG entropy (16 random bytes).
- Encode as unpadded RFC 4648 Base32 using uppercase A-Z and 2-7; display once only. UI may group characters with hyphens for readability.
- Normalize by uppercasing and removing only the documented grouping hyphens; reject any other character or malformed length.
- Persist only SHA-256 over a domain-separated value containing a fixed recovery-code label, the owning user ID and normalized code. No plaintext or reversible encryption is stored. Given at least 128 bits of entropy, a password KDF is not required for offline guessing resistance; retain constant-time verification/consumption behavior.
- A recovery code is consumed atomically with MFA transaction consumption and session/refresh creation. It is one-time and cannot be reused concurrently.
- Display each generated code once; never return it from later APIs or log it.
- All invalid, unknown, used and wrong-owner codes produce the same public failure. Do not expose match/no-match details or provide a code-enumeration oracle.
- Recovery-code generation, use, reset and administrative recovery are audited.

SMS is not an acceptable Staff second factor. No support/admin override may silently bypass MFA.

## 7. Cookie names, attributes and clearing behavior

Production cookies are issued by api.shop.com as host-only cookies. No Domain attribute is ever set.

| Purpose | Exact production cookie name |
|---|---|
| Access JWT | __Host-access-token |
| Refresh token | __Host-refresh-token |
| Staff MFA transaction | __Host-staff-mfa |

All three production cookies use Secure, HttpOnly, SameSite=Lax and Path=/. Their expiry is explicit and never exceeds the underlying credential/transaction expiry. The __Host- prefix is valid because Secure is set, Path is exactly /, and Domain is omitted. Access and refresh remain separate cookies.

Development over local HTTP uses separate names: dev-access-token, dev-refresh-token and dev-staff-mfa. These names are permitted only in the Development environment and only for the local HTTP origin. Secure=false is allowed only there. Production/Staging must reject development cookie configuration and must never emit dev-prefixed cookies. Production must never fall back to non-Secure cookies.

Cookie lifecycle:
- Successful Customer password or OTP login: set access + refresh; clear any stale staff-MFA cookie.
- Successful Staff MFA: set access + refresh; clear staff-MFA cookie.
- Successful Staff password pre-authentication: set/replace only staff-MFA; do not set or clear normal session cookies.
- Failed password login or failed OTP request/verification: do not clear an unrelated existing authenticated session cookie. Keep the MFA cookie only while its transaction remains valid and attempts remain.
- Expired, consumed, cancelled or attempt-exhausted MFA transaction: clear staff-MFA cookie.
- Refresh success: rotate refresh and replace access cookie; refresh failure due to invalid/expired/revoked/replayed credentials clears access and refresh cookies.
- Logout: revoke current session and clear access, refresh and staff-MFA cookies.
- Sign-out-all-devices clears current browser's access/refresh cookies after revoking all applicable sessions.
- Cookie deletion must use the same name, Path and host-only scope as issuance, with an expired value/Max-Age=0; never add Domain during deletion.

## 8. OTP security and abuse controls

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

OTP purpose in this slice is specifically Customer login. Registration/phone-verification purposes are separate flows and must not be accepted by the login verifier.

## 9. Failure, concurrency and authorization invariants

- Customer and Staff endpoints enforce AccountType server-side.
- Customer email/mobile lookup never falls back to Staff accounts; Staff username lookup never falls back to Customer accounts.
- Unconfirmed email/mobile cannot authenticate by password; unverified mobile cannot receive or verify login OTP.
- OTP verification and Staff MFA completion are single-use under concurrency.
- Challenge/MFA consumption, recovery-code consumption where applicable, AuthSession insertion and initial RefreshToken insertion are atomic.
- A successful ceremony creates exactly one session and one initial active refresh token.
- Session creation and refresh rotation use consistent session/token lock ordering: session first, then token.
- Self-session operations are ownership-scoped for both Customer and Staff. Security-admin Staff-to-Staff revocation is separately permissioned and audited.
- Current roles are loaded from server-side Identity data; no client role/account classification is trusted.
- Staff endpoints and Staff sessions require Staff AccountType, the required role/policy and completed MFA.
- Stateless access JWT revocation remains bounded by its approved maximum lifetime plus clock skew.

## 10. Required verification

### Identifier and account separation
- Email normalization, case-insensitive lookup and duplicate normalized Customer email rejection at PostgreSQL level.
- Iranian mobile numeral normalization and accepted/rejected input formats.
- Canonical phone uniqueness for Customer accounts, including concurrent writes.
- Exact partial-index predicates, NULL/empty-value checks, and migration failure on existing duplicates.
- AccountType conversion collision rolls back both conversion and session revocation; successful conversion revokes all sessions atomically.
- Customer rejects Staff and Staff rejects Customer; Customer username is never a login identifier.

### Customer password and OTP
- Correct/incorrect password, disabled/locked account, unknown identifier and unconfirmed identifier.
- Generic failure response and no account enumeration.
- OTP purpose binding, expiry, resend invalidation, single use, max attempts, wrong-code behavior and concurrent verification.
- OTP challenge consumption + session + initial refresh insertion rollback together on forced failure.
- Concurrent duplicate OTP verification creates at most one session.
- Quotas under concurrent requests and multiple API instances; provider timeout/failure/circuit breaker and no unbounded retry.
- Raw password, OTP, phone and token values absent from logs/traces.

### Staff MFA
- Password-only Staff login creates no AuthSession/access/refresh cookie.
- Customer cannot enter Staff MFA flow.
- TOTP RFC profile test vectors, past/current/future window boundaries, stale step rejection and concurrent duplicate-step rejection.
- MFA transaction consumption + TOTP step advance + session + initial refresh insertion roll back together on forced failure.
- Recovery-code entropy/encoding, one-time display, normalization, wrong-owner rejection, reuse rejection and concurrent consumption.
- Recovery-code consumption + MFA transaction + session + initial refresh insertion roll back together on forced failure.
- Generic failures and no recovery-code enumeration.
- TOTP secret encryption at rest and no secret disclosure.
- Staff session is created only after MFA success.

### Session, cookie, browser and authorization
- Correct Customer/Staff lifetime selection and absolute-expiry cap.
- Exactly one initial refresh token per new session; hash-only persistence.
- RS256, key ID, issuer/audience/lifetime validation and five-minute maximum.
- Production and Development cookie names/attributes; production never emits dev names or insecure cookies.
- Cookie clearing behavior on refresh failure, logout, MFA completion and terminal MFA failure.
- Lost-response orphan session remains bounded by initial idle expiry and cleanup.
- CSRF token, missing/hostile Origin and exact CORS allow-list.
- Refresh concurrency/replay, logout and session revocation regression tests.
- Customer and Staff self-session ownership, current-session protection, security-admin permission and audit coverage.
- Role matrix, horizontal/vertical escalation and IDOR.
- Build, unit tests and PostgreSQL-backed integration tests pass in CI.

## 11. Implementation sequence and gates

1. **2C-Design (this document):** approve endpoint, normalization, persistence and ceremony contracts before implementation.
2. **2C-Foundation:** shared validated session policy and session-creation service; initial refresh token + access-token issuance boundary; unit/PostgreSQL tests.
3. **2C-Customer:** email/mobile password login and verified-mobile OTP request/verify; uniqueness and anti-enumeration tests.
4. **2C-Staff:** password pre-authentication transaction, TOTP verification, replay prevention and recovery-code consumption.
5. **2C-Completion:** cookie/CSRF/Origin/CORS integration, authorization policies, Customer/Staff session management endpoints and end-to-end tests.
6. **2C-Security review:** inspect full diff, threat model, concurrency coverage, CI and debt register; no merge without explicit owner approval.

Each implementation slice is a separate reviewable PR or an explicitly agreed small set of PRs. Every PR updates progress and any technical-debt rows it introduces. Do not begin the next slice until the current slice's tests and review are complete and the owner approves proceeding.

## 12. Explicit non-goals and tracked production gates

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
).

The unique global normalized-username index remains in force for both account types. Staff contact email/phone may overlap Customer login identifiers because the Customer partial indexes intentionally scope only AccountType = 1.

Changing AccountType is a privileged, audited operation in one PostgreSQL transaction. The update must satisfy all destination-type uniqueness constraints; any collision aborts the entire transaction. Successful account-type change revokes all sessions in the same transaction. No application check-then-write is authoritative.

## 4. Endpoint contracts

All routes are under /api/v1/auth. DTOs contain no token fields. Credentials are returned only through response cookies after successful completion.

| Method and path | Purpose | Session created? |
|---|---|---:|
| POST /customer/password/login | Customer email/mobile + password | Yes, on success |
| POST /customer/otp/request | Request login OTP for verified Customer mobile | No |
| POST /customer/otp/verify | Verify purpose-bound Customer login OTP | Yes, on success |
| POST /staff/login | Staff username + password | No |
| POST /staff/mfa/verify | Complete Staff TOTP/recovery ceremony | Yes, on success |
| POST /refresh | Rotate refresh token and issue access token | Existing session |
| POST /logout | Revoke current session and clear cookies | Revokes |
| GET /sessions | List the current actor's own sessions | No |
| DELETE /sessions/{sessionId} | Revoke an owned non-current session | No |
| DELETE /staff/{staffUserId}/sessions/{sessionId} | Security-admin revocation of a Staff session | No |

The self-session routes are actor-generic: the authenticated Customer or Staff actor can list only their own sessions and revoke only their own non-current sessions. The response identifies the current session. Neither actor can revoke the current session through the single-session route; use logout or the explicit sign-out-all-devices action for that. Customer sign-out-all revokes all of that Customer's sessions, including current. Staff self-service sign-out-all follows the same rule.

The staff-to-staff route requires a dedicated SecurityAdmin permission, Staff AccountType and completed MFA. It verifies that the target session belongs to the target Staff user, prevents cross-account IDOR, records actor/target/session/reason in the audit log and performs revocation atomically. It does not permit a Customer actor or ordinary Staff role to revoke another Staff member's sessions.

Every unsafe cookie-authenticated request requires the approved antiforgery request token and exact trusted Origin validation. Login and OTP-request endpoints remain subject to exact Origin/CORS policy and endpoint-specific abuse controls.

### Customer password

Request contains identifier and password. The identifier may be email or one of the supported mobile formats. Do not accept accountType, userId, role, session lifetime, authentication method or verification flags from the client.

Success creates a session and sets cookies; JSON contains only a minimal safe account result and no raw token. Failure is generic for unknown identifier, wrong password, unconfirmed identifier, disabled/locked account or account-type mismatch.

### Customer OTP

OTP request accepts mobileNumber. Its public response is generic for every outcome. Apply phone/IP/global quotas and risk controls; only a verified Customer mobile receives a login-purpose challenge.

OTP verification accepts mobileNumber and code. The server resolves the canonical mobile and active Customer-login challenge. The challenge is purpose-bound, time-bounded, attempt-limited, single-use and atomic. Concurrent submissions cannot create multiple sessions from one challenge.

### Staff password and MFA

Staff login accepts username and password. Success creates a single-use MFA transaction and sets only the dedicated MFA cookie. It does not authenticate the browser or issue normal session credentials.

MFA verification accepts either a TOTP code or a recovery code. The server resolves the pending transaction from the dedicated cookie; never accept client-supplied user ID or transaction owner. Validate purpose, expiry, cancellation/consumption state and bounded failed attempts.

## 5. Shared session creation and transaction boundary

All completed ceremonies use one server-side session-creation service. It accepts only trusted server-side user ID, persisted AccountType, server-selected AuthenticationMethod, validated lifetime policy, database time and privacy-reviewed device metadata.

Authentication-method mapping:
- Customer password via email or mobile -> Password.
- Customer SMS OTP -> SmsOtp.
- Staff password + TOTP -> PasswordTotp.
- Staff password + recovery code -> PasswordRecoveryCode.

### Required atomicity

For Customer OTP and Staff MFA completion, the following state changes MUST commit or roll back together in one PostgreSQL transaction using the same IdentityDbContext/connection:

1. Lock and revalidate the OTP challenge or Staff MFA transaction.
2. For Staff TOTP, atomically advance the authenticator's last accepted time-step; for recovery, atomically consume the selected recovery code.
3. Consume the OTP challenge or Staff MFA transaction.
4. Revalidate account enabled state, expected AccountType and current authorization state.
5. Insert exactly one AuthSession.
6. Insert exactly one initial opaque 256-bit RefreshToken, persisting only SHA-256(secret).

No one-time factor may be consumed without the session and initial refresh token being committed. No session may be committed while the factor remains reusable. Password-based Customer session creation uses the same session/refresh transaction but has no one-time challenge to consume.

Generate session identifiers, refresh secret and token claims from server-side state. Access JWT signing must complete before the database transaction commits so signing failure rolls back factor consumption and session persistence. Signing in this transaction must be a bounded local signing operation; do not perform an unbounded remote network call while holding database locks. Cookie serialization/options must be validated before commit. Cookies are appended only after a successful commit. If commit fails, no credentials are returned.

If the database commit succeeds but the HTTP response is lost before the browser receives cookies, the resulting session is unreachable to that browser. Do not weaken strict refresh replay or add a retry grace period. The orphan remains bounded by the session's initial idle expiry (Customer: at most seven days; Staff: at most thirty minutes) and absolute expiry; a cleanup job must revoke/expire sessions that have passed idle/absolute expiry and remove expired refresh credentials according to retention policy. The user can authenticate again, creating a separate session. Do not attempt to infer successful cookie delivery from server-side response completion.

### Session policy

- Customer: 30-day absolute / 7-day idle.
- Staff: 12-hour absolute / 30-minute idle.
- Absolute expiry never slides; idle expiry extends only after successful refresh.
- Use UTC/database time and enforce IdleExpiresAt <= AbsoluteExpiresAt.
- Load current roles from server-side Identity data; never copy roles or assurance from client/JWT state.
- Access JWT is RS256, maximum five minutes, with approved minimal claims and server-issued role claims.
- Session and initial refresh-token insertion are atomic. Persist only SHA-256(refresh secret), never the raw secret.
- No raw access/refresh token in JSON, logs, traces or exceptions.

## 6. Staff MFA, TOTP and recovery codes

### TOTP profile

Use RFC 6238 with this fixed initial interoperability profile:
- HMAC-SHA-1.
- Six decimal digits.
- 30-second time-step.
- T0 = 0 (Unix epoch).
- Accept at most one step in the past and one step in the future (±1); do not widen dynamically.
- Use a cryptographically secure random TOTP secret; encrypt it at rest using the approved Data Protection boundary or a reviewed equivalent. Never return it after enrollment or log it.
- Enrollment remains pending until the first generated code is successfully verified.

Persist LastAcceptedTimeStep per authenticator. A submitted code is valid only if its matching step is greater than LastAcceptedTimeStep. If more than one candidate step matches, select the greatest matching step that is still within the allowed window and greater than the persisted value. Advance the value with a conditional database update or row lock in the same transaction as MFA transaction consumption and session creation. A concurrent duplicate submission must have at most one winner. A code from an already accepted step is rejected even while it remains inside the clock-skew window.

If a future implementation changes algorithm/digits/period, it is a new reviewed interoperability decision and must update authenticator enrollment metadata and URI parameters; do not silently change this profile.

### MFA transaction

- Lifetime is server-configured but never greater than five minutes.
- Pre-authentication secret is cryptographically random, opaque and high entropy; persist only a hash.
- Cookie is __Host-staff-mfa; production attributes are Secure, HttpOnly, SameSite=Lax, Path=/, no Domain, maximum five-minute lifetime.
- Transaction is bound to one Staff user and one fixed purpose.
- It can only complete/cancel that transaction; it cannot be upgraded without successful MFA.
- Failed attempts are bounded. Consume/cancel/expire atomically.
- Password success does not reset or bypass MFA attempt policy.

### Recovery codes

- Generate each code from at least 128 bits of CSPRNG entropy (16 random bytes).
- Encode as unpadded RFC 4648 Base32 using uppercase A-Z and 2-7; display once only. UI may group characters with hyphens for readability.
- Normalize by uppercasing and removing only the documented grouping hyphens; reject any other character or malformed length.
- Persist only SHA-256 over the exact UTF-8 byte sequence: ASCII label 'staff-recovery-v1', one 0x00 byte, the owning user UUID in lowercase 32-character N format, one 0x00 byte, then the normalized Base32 code. No plaintext or reversible encryption is stored. Given at least 128 bits of entropy, a password KDF is not required for offline guessing resistance; retain constant-time verification/consumption behavior.
- A recovery code is consumed atomically with MFA transaction consumption and session/refresh creation. It is one-time and cannot be reused concurrently.
- Display each generated code once; never return it from later APIs or log it.
- All invalid, unknown, used and wrong-owner codes produce the same public failure. Do not expose match/no-match details or provide a code-enumeration oracle.
- Recovery-code generation, use, reset and administrative recovery are audited.

SMS is not an acceptable Staff second factor. No support/admin override may silently bypass MFA.

## 7. Cookie names, attributes and clearing behavior

Production cookies are issued by api.shop.com as host-only cookies. No Domain attribute is ever set.

| Purpose | Exact production cookie name |
|---|---|
| Access JWT | __Host-access-token |
| Refresh token | __Host-refresh-token |
| Staff MFA transaction | __Host-staff-mfa |

All three production cookies use Secure, HttpOnly, SameSite=Lax and Path=/. Their expiry is explicit and never exceeds the underlying credential/transaction expiry. The __Host- prefix is valid because Secure is set, Path is exactly /, and Domain is omitted. Access and refresh remain separate cookies.

Development over local HTTP uses separate names: dev-access-token, dev-refresh-token and dev-staff-mfa. These names are permitted only in the Development environment and only for the local HTTP origin. Secure=false is allowed only there. Production/Staging must reject development cookie configuration and must never emit dev-prefixed cookies. Production must never fall back to non-Secure cookies.

Cookie lifecycle:
- Successful Customer password or OTP login: set access + refresh; clear any stale staff-MFA cookie.
- Successful Staff MFA: set access + refresh; clear staff-MFA cookie.
- Successful Staff password pre-authentication: set/replace only staff-MFA; do not set or clear normal session cookies.
- Failed password login or failed OTP request/verification: do not clear an unrelated existing authenticated session cookie. Keep the MFA cookie only while its transaction remains valid and attempts remain.
- Expired, consumed, cancelled or attempt-exhausted MFA transaction: clear staff-MFA cookie.
- Refresh success: rotate refresh and replace access cookie; refresh failure due to invalid/expired/revoked/replayed credentials clears access and refresh cookies.
- Logout: revoke current session and clear access, refresh and staff-MFA cookies.
- Sign-out-all-devices clears current browser's access/refresh cookies after revoking all applicable sessions.
- Cookie deletion must use the same name, Path and host-only scope as issuance, with an expired value/Max-Age=0; never add Domain during deletion.

## 8. OTP security and abuse controls

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

OTP purpose in this slice is specifically Customer login. Registration/phone-verification purposes are separate flows and must not be accepted by the login verifier.

## 9. Failure, concurrency and authorization invariants

- Customer and Staff endpoints enforce AccountType server-side.
- Customer email/mobile lookup never falls back to Staff accounts; Staff username lookup never falls back to Customer accounts.
- Unconfirmed email/mobile cannot authenticate by password; unverified mobile cannot receive or verify login OTP.
- OTP verification and Staff MFA completion are single-use under concurrency.
- Challenge/MFA consumption, recovery-code consumption where applicable, AuthSession insertion and initial RefreshToken insertion are atomic.
- A successful ceremony creates exactly one session and one initial active refresh token.
- Session creation and refresh rotation use consistent session/token lock ordering: session first, then token.
- Self-session operations are ownership-scoped for both Customer and Staff. Security-admin Staff-to-Staff revocation is separately permissioned and audited.
- Current roles are loaded from server-side Identity data; no client role/account classification is trusted.
- Staff endpoints and Staff sessions require Staff AccountType, the required role/policy and completed MFA.
- Stateless access JWT revocation remains bounded by its approved maximum lifetime plus clock skew.

## 10. Required verification

### Identifier and account separation
- Email normalization, case-insensitive lookup and duplicate normalized Customer email rejection at PostgreSQL level.
- Iranian mobile numeral normalization and accepted/rejected input formats.
- Canonical phone uniqueness for Customer accounts, including concurrent writes.
- Exact partial-index predicates, NULL/empty-value checks, and migration failure on existing duplicates.
- AccountType conversion collision rolls back both conversion and session revocation; successful conversion revokes all sessions atomically.
- Customer rejects Staff and Staff rejects Customer; Customer username is never a login identifier.

### Customer password and OTP
- Correct/incorrect password, disabled/locked account, unknown identifier and unconfirmed identifier.
- Generic failure response and no account enumeration.
- OTP purpose binding, expiry, resend invalidation, single use, max attempts, wrong-code behavior and concurrent verification.
- OTP challenge consumption + session + initial refresh insertion rollback together on forced failure.
- Concurrent duplicate OTP verification creates at most one session.
- Quotas under concurrent requests and multiple API instances; provider timeout/failure/circuit breaker and no unbounded retry.
- Raw password, OTP, phone and token values absent from logs/traces.

### Staff MFA
- Password-only Staff login creates no AuthSession/access/refresh cookie.
- Customer cannot enter Staff MFA flow.
- TOTP RFC profile test vectors, past/current/future window boundaries, stale step rejection and concurrent duplicate-step rejection.
- MFA transaction consumption + TOTP step advance + session + initial refresh insertion roll back together on forced failure.
- Recovery-code entropy/encoding, one-time display, normalization, wrong-owner rejection, reuse rejection and concurrent consumption.
- Recovery-code consumption + MFA transaction + session + initial refresh insertion roll back together on forced failure.
- Generic failures and no recovery-code enumeration.
- TOTP secret encryption at rest and no secret disclosure.
- Staff session is created only after MFA success.

### Session, cookie, browser and authorization
- Correct Customer/Staff lifetime selection and absolute-expiry cap.
- Exactly one initial refresh token per new session; hash-only persistence.
- RS256, key ID, issuer/audience/lifetime validation and five-minute maximum.
- Production and Development cookie names/attributes; production never emits dev names or insecure cookies.
- Cookie clearing behavior on refresh failure, logout, MFA completion and terminal MFA failure.
- Lost-response orphan session remains bounded by initial idle expiry and cleanup.
- CSRF token, missing/hostile Origin and exact CORS allow-list.
- Refresh concurrency/replay, logout and session revocation regression tests.
- Customer and Staff self-session ownership, current-session protection, security-admin permission and audit coverage.
- Role matrix, horizontal/vertical escalation and IDOR.
- Build, unit tests and PostgreSQL-backed integration tests pass in CI.

## 11. Implementation sequence and gates

1. **2C-Design (this document):** approve endpoint, normalization, persistence and ceremony contracts before implementation.
2. **2C-Foundation:** shared validated session policy and session-creation service; initial refresh token + access-token issuance boundary; unit/PostgreSQL tests.
3. **2C-Customer:** email/mobile password login and verified-mobile OTP request/verify; uniqueness and anti-enumeration tests.
4. **2C-Staff:** password pre-authentication transaction, TOTP verification, replay prevention and recovery-code consumption.
5. **2C-Completion:** cookie/CSRF/Origin/CORS integration, authorization policies, Customer/Staff session management endpoints and end-to-end tests.
6. **2C-Security review:** inspect full diff, threat model, concurrency coverage, CI and debt register; no merge without explicit owner approval.

Each implementation slice is a separate reviewable PR or an explicitly agreed small set of PRs. Every PR updates progress and any technical-debt rows it introduces. Do not begin the next slice until the current slice's tests and review are complete and the owner approves proceeding.

## 12. Explicit non-goals and tracked production gates

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
). The application must keep Email and NormalizedEmail nullability aligned; the mobile check enforces the canonical Iranian E.164 shape at the database boundary. These checks make the partial-index predicates exact and avoid empty values being treated as identifiers. Existing rows must be audited and normalized before creating the indexes; migration must fail atomically on conflicting Customer identifiers rather than silently choosing a winner.

The unique global normalized-username index remains in force for both account types. Staff contact email/phone may overlap Customer login identifiers because the Customer partial indexes intentionally scope only AccountType = 1.

Changing AccountType is a privileged, audited operation in one PostgreSQL transaction. The update must satisfy all destination-type uniqueness constraints; any collision aborts the entire transaction. Successful account-type change revokes all sessions in the same transaction. No application check-then-write is authoritative.

## 4. Endpoint contracts

All routes are under /api/v1/auth. DTOs contain no token fields. Credentials are returned only through response cookies after successful completion.

| Method and path | Purpose | Session created? |
|---|---|---:|
| POST /customer/password/login | Customer email/mobile + password | Yes, on success |
| POST /customer/otp/request | Request login OTP for verified Customer mobile | No |
| POST /customer/otp/verify | Verify purpose-bound Customer login OTP | Yes, on success |
| POST /staff/login | Staff username + password | No |
| POST /staff/mfa/verify | Complete Staff TOTP/recovery ceremony | Yes, on success |
| POST /refresh | Rotate refresh token and issue access token | Existing session |
| POST /logout | Revoke current session and clear cookies | Revokes |
| GET /sessions | List the current actor's own sessions | No |
| DELETE /sessions/{sessionId} | Revoke an owned non-current session | No |
| DELETE /staff/{staffUserId}/sessions/{sessionId} | Security-admin revocation of a Staff session | No |

The self-session routes are actor-generic: the authenticated Customer or Staff actor can list only their own sessions and revoke only their own non-current sessions. The response identifies the current session. Neither actor can revoke the current session through the single-session route; use logout or the explicit sign-out-all-devices action for that. Customer sign-out-all revokes all of that Customer's sessions, including current. Staff self-service sign-out-all follows the same rule.

The staff-to-staff route requires a dedicated SecurityAdmin permission, Staff AccountType and completed MFA. It verifies that the target session belongs to the target Staff user, prevents cross-account IDOR, records actor/target/session/reason in the audit log and performs revocation atomically. It does not permit a Customer actor or ordinary Staff role to revoke another Staff member's sessions.

Every unsafe cookie-authenticated request requires the approved antiforgery request token and exact trusted Origin validation. Login and OTP-request endpoints remain subject to exact Origin/CORS policy and endpoint-specific abuse controls.

### Customer password

Request contains identifier and password. The identifier may be email or one of the supported mobile formats. Do not accept accountType, userId, role, session lifetime, authentication method or verification flags from the client.

Success creates a session and sets cookies; JSON contains only a minimal safe account result and no raw token. Failure is generic for unknown identifier, wrong password, unconfirmed identifier, disabled/locked account or account-type mismatch.

### Customer OTP

OTP request accepts mobileNumber. Its public response is generic for every outcome. Apply phone/IP/global quotas and risk controls; only a verified Customer mobile receives a login-purpose challenge.

OTP verification accepts mobileNumber and code. The server resolves the canonical mobile and active Customer-login challenge. The challenge is purpose-bound, time-bounded, attempt-limited, single-use and atomic. Concurrent submissions cannot create multiple sessions from one challenge.

### Staff password and MFA

Staff login accepts username and password. Success creates a single-use MFA transaction and sets only the dedicated MFA cookie. It does not authenticate the browser or issue normal session credentials.

MFA verification accepts either a TOTP code or a recovery code. The server resolves the pending transaction from the dedicated cookie; never accept client-supplied user ID or transaction owner. Validate purpose, expiry, cancellation/consumption state and bounded failed attempts.

## 5. Shared session creation and transaction boundary

All completed ceremonies use one server-side session-creation service. It accepts only trusted server-side user ID, persisted AccountType, server-selected AuthenticationMethod, validated lifetime policy, database time and privacy-reviewed device metadata.

Authentication-method mapping:
- Customer password via email or mobile -> Password.
- Customer SMS OTP -> SmsOtp.
- Staff password + TOTP -> PasswordTotp.
- Staff password + recovery code -> PasswordRecoveryCode.

### Required atomicity

For Customer OTP and Staff MFA completion, the following state changes MUST commit or roll back together in one PostgreSQL transaction using the same IdentityDbContext/connection:

1. Lock and revalidate the OTP challenge or Staff MFA transaction.
2. For Staff TOTP, atomically advance the authenticator's last accepted time-step; for recovery, atomically consume the selected recovery code.
3. Consume the OTP challenge or Staff MFA transaction.
4. Revalidate account enabled state, expected AccountType and current authorization state.
5. Insert exactly one AuthSession.
6. Insert exactly one initial opaque 256-bit RefreshToken, persisting only SHA-256(secret).

No one-time factor may be consumed without the session and initial refresh token being committed. No session may be committed while the factor remains reusable. Password-based Customer session creation uses the same session/refresh transaction but has no one-time challenge to consume.

Generate session identifiers, refresh secret and token claims from server-side state. Access JWT signing must complete before the database transaction commits so signing failure rolls back factor consumption and session persistence. Signing in this transaction must be a bounded local signing operation; do not perform an unbounded remote network call while holding database locks. Cookie serialization/options must be validated before commit. Cookies are appended only after a successful commit. If commit fails, no credentials are returned.

If the database commit succeeds but the HTTP response is lost before the browser receives cookies, the resulting session is unreachable to that browser. Do not weaken strict refresh replay or add a retry grace period. The orphan remains bounded by the session's initial idle expiry (Customer: at most seven days; Staff: at most thirty minutes) and absolute expiry; a cleanup job must revoke/expire sessions that have passed idle/absolute expiry and remove expired refresh credentials according to retention policy. The user can authenticate again, creating a separate session. Do not attempt to infer successful cookie delivery from server-side response completion.

### Session policy

- Customer: 30-day absolute / 7-day idle.
- Staff: 12-hour absolute / 30-minute idle.
- Absolute expiry never slides; idle expiry extends only after successful refresh.
- Use UTC/database time and enforce IdleExpiresAt <= AbsoluteExpiresAt.
- Load current roles from server-side Identity data; never copy roles or assurance from client/JWT state.
- Access JWT is RS256, maximum five minutes, with approved minimal claims and server-issued role claims.
- Session and initial refresh-token insertion are atomic. Persist only SHA-256(refresh secret), never the raw secret.
- No raw access/refresh token in JSON, logs, traces or exceptions.

## 6. Staff MFA, TOTP and recovery codes

### TOTP profile

Use RFC 6238 with this fixed initial interoperability profile:
- HMAC-SHA-1.
- Six decimal digits.
- 30-second time-step.
- T0 = 0 (Unix epoch).
- Accept at most one step in the past and one step in the future (±1); do not widen dynamically.
- Use a cryptographically secure random TOTP secret; encrypt it at rest using the approved Data Protection boundary or a reviewed equivalent. Never return it after enrollment or log it.
- Enrollment remains pending until the first generated code is successfully verified.

Persist LastAcceptedTimeStep per authenticator. A submitted code is valid only if its matching step is greater than LastAcceptedTimeStep. If more than one candidate step matches, select the greatest matching step that is still within the allowed window and greater than the persisted value. Advance the value with a conditional database update or row lock in the same transaction as MFA transaction consumption and session creation. A concurrent duplicate submission must have at most one winner. A code from an already accepted step is rejected even while it remains inside the clock-skew window.

If a future implementation changes algorithm/digits/period, it is a new reviewed interoperability decision and must update authenticator enrollment metadata and URI parameters; do not silently change this profile.

### MFA transaction

- Lifetime is server-configured but never greater than five minutes.
- Pre-authentication secret is cryptographically random, opaque and high entropy; persist only a hash.
- Cookie is __Host-staff-mfa; production attributes are Secure, HttpOnly, SameSite=Lax, Path=/, no Domain, maximum five-minute lifetime.
- Transaction is bound to one Staff user and one fixed purpose.
- It can only complete/cancel that transaction; it cannot be upgraded without successful MFA.
- Failed attempts are bounded. Consume/cancel/expire atomically.
- Password success does not reset or bypass MFA attempt policy.

### Recovery codes

- Generate each code from at least 128 bits of CSPRNG entropy (16 random bytes).
- Encode as unpadded RFC 4648 Base32 using uppercase A-Z and 2-7; display once only. UI may group characters with hyphens for readability.
- Normalize by uppercasing and removing only the documented grouping hyphens; reject any other character or malformed length.
- Persist only SHA-256 over a domain-separated value containing a fixed recovery-code label, the owning user ID and normalized code. No plaintext or reversible encryption is stored. Given at least 128 bits of entropy, a password KDF is not required for offline guessing resistance; retain constant-time verification/consumption behavior.
- A recovery code is consumed atomically with MFA transaction consumption and session/refresh creation. It is one-time and cannot be reused concurrently.
- Display each generated code once; never return it from later APIs or log it.
- All invalid, unknown, used and wrong-owner codes produce the same public failure. Do not expose match/no-match details or provide a code-enumeration oracle.
- Recovery-code generation, use, reset and administrative recovery are audited.

SMS is not an acceptable Staff second factor. No support/admin override may silently bypass MFA.

## 7. Cookie names, attributes and clearing behavior

Production cookies are issued by api.shop.com as host-only cookies. No Domain attribute is ever set.

| Purpose | Exact production cookie name |
|---|---|
| Access JWT | __Host-access-token |
| Refresh token | __Host-refresh-token |
| Staff MFA transaction | __Host-staff-mfa |

All three production cookies use Secure, HttpOnly, SameSite=Lax and Path=/. Their expiry is explicit and never exceeds the underlying credential/transaction expiry. The __Host- prefix is valid because Secure is set, Path is exactly /, and Domain is omitted. Access and refresh remain separate cookies.

Development over local HTTP uses separate names: dev-access-token, dev-refresh-token and dev-staff-mfa. These names are permitted only in the Development environment and only for the local HTTP origin. Secure=false is allowed only there. Production/Staging must reject development cookie configuration and must never emit dev-prefixed cookies. Production must never fall back to non-Secure cookies.

Cookie lifecycle:
- Successful Customer password or OTP login: set access + refresh; clear any stale staff-MFA cookie.
- Successful Staff MFA: set access + refresh; clear staff-MFA cookie.
- Successful Staff password pre-authentication: set/replace only staff-MFA; do not set or clear normal session cookies.
- Failed password login or failed OTP request/verification: do not clear an unrelated existing authenticated session cookie. Keep the MFA cookie only while its transaction remains valid and attempts remain.
- Expired, consumed, cancelled or attempt-exhausted MFA transaction: clear staff-MFA cookie.
- Refresh success: rotate refresh and replace access cookie; refresh failure due to invalid/expired/revoked/replayed credentials clears access and refresh cookies.
- Logout: revoke current session and clear access, refresh and staff-MFA cookies.
- Sign-out-all-devices clears current browser's access/refresh cookies after revoking all applicable sessions.
- Cookie deletion must use the same name, Path and host-only scope as issuance, with an expired value/Max-Age=0; never add Domain during deletion.

## 8. OTP security and abuse controls

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

OTP purpose in this slice is specifically Customer login. Registration/phone-verification purposes are separate flows and must not be accepted by the login verifier.

## 9. Failure, concurrency and authorization invariants

- Customer and Staff endpoints enforce AccountType server-side.
- Customer email/mobile lookup never falls back to Staff accounts; Staff username lookup never falls back to Customer accounts.
- Unconfirmed email/mobile cannot authenticate by password; unverified mobile cannot receive or verify login OTP.
- OTP verification and Staff MFA completion are single-use under concurrency.
- Challenge/MFA consumption, recovery-code consumption where applicable, AuthSession insertion and initial RefreshToken insertion are atomic.
- A successful ceremony creates exactly one session and one initial active refresh token.
- Session creation and refresh rotation use consistent session/token lock ordering: session first, then token.
- Self-session operations are ownership-scoped for both Customer and Staff. Security-admin Staff-to-Staff revocation is separately permissioned and audited.
- Current roles are loaded from server-side Identity data; no client role/account classification is trusted.
- Staff endpoints and Staff sessions require Staff AccountType, the required role/policy and completed MFA.
- Stateless access JWT revocation remains bounded by its approved maximum lifetime plus clock skew.

## 10. Required verification

### Identifier and account separation
- Email normalization, case-insensitive lookup and duplicate normalized Customer email rejection at PostgreSQL level.
- Iranian mobile numeral normalization and accepted/rejected input formats.
- Canonical phone uniqueness for Customer accounts, including concurrent writes.
- Exact partial-index predicates, NULL/empty-value checks, and migration failure on existing duplicates.
- AccountType conversion collision rolls back both conversion and session revocation; successful conversion revokes all sessions atomically.
- Customer rejects Staff and Staff rejects Customer; Customer username is never a login identifier.

### Customer password and OTP
- Correct/incorrect password, disabled/locked account, unknown identifier and unconfirmed identifier.
- Generic failure response and no account enumeration.
- OTP purpose binding, expiry, resend invalidation, single use, max attempts, wrong-code behavior and concurrent verification.
- OTP challenge consumption + session + initial refresh insertion rollback together on forced failure.
- Concurrent duplicate OTP verification creates at most one session.
- Quotas under concurrent requests and multiple API instances; provider timeout/failure/circuit breaker and no unbounded retry.
- Raw password, OTP, phone and token values absent from logs/traces.

### Staff MFA
- Password-only Staff login creates no AuthSession/access/refresh cookie.
- Customer cannot enter Staff MFA flow.
- TOTP RFC profile test vectors, past/current/future window boundaries, stale step rejection and concurrent duplicate-step rejection.
- MFA transaction consumption + TOTP step advance + session + initial refresh insertion roll back together on forced failure.
- Recovery-code entropy/encoding, one-time display, normalization, wrong-owner rejection, reuse rejection and concurrent consumption.
- Recovery-code consumption + MFA transaction + session + initial refresh insertion roll back together on forced failure.
- Generic failures and no recovery-code enumeration.
- TOTP secret encryption at rest and no secret disclosure.
- Staff session is created only after MFA success.

### Session, cookie, browser and authorization
- Correct Customer/Staff lifetime selection and absolute-expiry cap.
- Exactly one initial refresh token per new session; hash-only persistence.
- RS256, key ID, issuer/audience/lifetime validation and five-minute maximum.
- Production and Development cookie names/attributes; production never emits dev names or insecure cookies.
- Cookie clearing behavior on refresh failure, logout, MFA completion and terminal MFA failure.
- Lost-response orphan session remains bounded by initial idle expiry and cleanup.
- CSRF token, missing/hostile Origin and exact CORS allow-list.
- Refresh concurrency/replay, logout and session revocation regression tests.
- Customer and Staff self-session ownership, current-session protection, security-admin permission and audit coverage.
- Role matrix, horizontal/vertical escalation and IDOR.
- Build, unit tests and PostgreSQL-backed integration tests pass in CI.

## 11. Implementation sequence and gates

1. **2C-Design (this document):** approve endpoint, normalization, persistence and ceremony contracts before implementation.
2. **2C-Foundation:** shared validated session policy and session-creation service; initial refresh token + access-token issuance boundary; unit/PostgreSQL tests.
3. **2C-Customer:** email/mobile password login and verified-mobile OTP request/verify; uniqueness and anti-enumeration tests.
4. **2C-Staff:** password pre-authentication transaction, TOTP verification, replay prevention and recovery-code consumption.
5. **2C-Completion:** cookie/CSRF/Origin/CORS integration, authorization policies, Customer/Staff session management endpoints and end-to-end tests.
6. **2C-Security review:** inspect full diff, threat model, concurrency coverage, CI and debt register; no merge without explicit owner approval.

Each implementation slice is a separate reviewable PR or an explicitly agreed small set of PRs. Every PR updates progress and any technical-debt rows it introduces. Do not begin the next slice until the current slice's tests and review are complete and the owner approves proceeding.

## 12. Explicit non-goals and tracked production gates

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
