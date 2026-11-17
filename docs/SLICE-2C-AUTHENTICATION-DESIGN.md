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

Not included: Customer registration, email confirmation, phone verification, Customer password reset/change, passkeys/WebAuthn, frontend implementation, SMS-vendor selection or production KMS/HSM selection. Staff account provisioning, administrator-controlled password creation/reset, and the Staff MFA enrollment/reset ceremony are included as backend contracts. This document is not a production security certification.

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

Password success for an enrolled Staff account creates only a short-lived, purpose-bound MFA transaction. It MUST NOT create an AuthSession, access JWT, refresh token or normal authentication cookie. A Staff account whose MFA enrollment is pending or temporarily locked cannot enter the normal MFA completion flow and cannot obtain a session.

Every endpoint enforces the persisted AccountType. Customer endpoints never fall back to Staff records; Staff endpoints never fall back to Customer records.

### Staff provisioning and MFA enrollment

Staff accounts are never self-registered. Only a SuperAdmin with the dedicated StaffProvisioning permission may create a Staff account, set its initial password, reset its password, or initiate/reset its MFA enrollment. These actions require completed SuperAdmin MFA, are audited with actor/target/reason, and are protected by CSRF plus exact Origin validation. Persist a non-null server-controlled `MustChangePassword` flag on the user. It is set to true whenever SuperAdmin creates an account or sets/resets its password; clients cannot set or clear it. It is cleared only by the employee completing the mandatory password-change ceremony.

The administrative API accepts the initial/reset password as an explicit credential input; it is never generated into a public response, included in an invitation link, logged, or sent through the SMS delivery pipeline. The SuperAdmin communicates it through an independently trusted channel. Initial and reset passwords are temporary credentials, not permanent credentials. After completing MFA, the employee MUST change the temporary password before receiving any normal session or application access. Password reset sets `MustChangePassword=true` and atomically revokes all target sessions and pending MFA transactions. It does not silently reset or bypass the enrolled TOTP factor. The new password must satisfy the configured Identity password policy and must differ from the temporary password; password change is audited.

For first-time MFA enrollment or an explicitly authorized MFA reset:
1. SuperAdmin initiates enrollment for one existing Staff account. The server creates a cryptographically random, single-use invitation token (at least 256 bits), stores only its hash, binds it to the target Staff user and StaffMfaEnrollment purpose, and sets a configurable 24-hour maximum expiry.
2. The raw invitation token/link is returned to the SuperAdmin exactly once for delivery to the employee. It must not be placed in URL query/path, logs, analytics, referrer data or ordinary audit fields. A browser may carry it in a URL fragment and POST it in the request body. The administrator delivers it through a trusted channel; it is not an SMS OTP.
3. The employee redeems the invitation together with their Staff username and administrator-provided password. The server verifies both credentials, invitation ownership, expiry and single-use state, then consumes the invitation and issues only a short-lived __Host-staff-enrollment restricted cookie. This credential permits enrollment only; it is not an AuthSession and grants no application API access.
4. With that restricted cookie, the employee starts enrollment. The server generates the TOTP secret, persists it encrypted as pending, and returns the otpauth provisioning URI/QR payload to the employee exactly once over HTTPS. The SuperAdmin never receives or can retrieve the TOTP secret.
5. The employee enters the first valid TOTP code. The server atomically confirms the pending authenticator, activates MFA, initializes replay state, generates recovery codes and returns those codes once to the employee. Enrollment completion creates no normal session; the employee must then perform regular username/password + TOTP login.
6. Enrollment invitation and pending-secret transactions are single-use, time-bounded and audited. If the invitation or pending enrollment expires, SuperAdmin must issue a new invitation. If a redemption/start response is lost, do not expose a token/secret retrieval endpoint; SuperAdmin restarts enrollment with a new invitation. If enrollment completion succeeds but the recovery-code response is lost, SuperAdmin must perform an explicit MFA reset so a new recovery-code set can be generated; old codes remain invalid. An enrolled Staff account undergoing MFA reset is placed in EnrollmentPending; all existing sessions and pending MFA transactions are revoked, and all old recovery codes/authenticators are invalidated atomically before a new invitation is issued. No Staff login is allowed until the new authenticator is confirmed.

The enrollment invitation has a maximum 24-hour lifetime; the restricted enrollment cookie and pending authenticator ceremony have a maximum 10-minute lifetime. Values are configurable downward, never upward beyond these limits. Failure, cancellation or expiry clears the enrollment cookie. There is no manual database seeding or support override for MFA enrollment. The post-MFA password-change cookie is separate, single-purpose, single-use, maximum 10 minutes, and is cleared on success, failure exhaustion, cancellation or expiry. It cannot be used for refresh, session listing, logout of another session or any business API.

## 3. Identifier and database uniqueness contract

### Email

- Trim surrounding whitespace and apply the application's versioned canonical email-normalization function. Do not apply provider-specific dot/plus-tag rewriting. The exact implementation and uniqueness semantics must be covered by tests.
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

Authentication routes are under /api/v1/auth; SuperAdmin staff-management routes are under /api/v1/admin. DTOs contain no access/refresh token fields. Credentials are returned only through response cookies after successful authentication; the one-time enrollment invitation is returned once to the initiating SuperAdmin.

| Method and path | Purpose | Session created? |
|---|---|---:|
| POST /customer/password/login | Customer email/mobile + password | Yes, on success |
| POST /customer/otp/request | Request login OTP for verified Customer mobile | No |
| POST /customer/otp/verify | Verify purpose-bound Customer login OTP | Yes, on success |
| POST /staff/login | Staff username + password | No |
| POST /staff/mfa/enrollment/redeem | Redeem SuperAdmin-issued invitation using Staff credentials | No |
| POST /staff/mfa/enrollment/start | Create pending TOTP secret and return one-time provisioning payload | No |
| POST /staff/mfa/enrollment/complete | Confirm first TOTP code, activate MFA and display recovery codes once | No |
| POST /staff/mfa/verify | Complete Staff TOTP/recovery ceremony | Yes, unless password change is required |
| POST /staff/password/change | Change temporary Staff password using restricted post-MFA credential | Yes, only after successful change |
| POST /refresh | Rotate refresh token and issue access token | Existing session |
| DELETE /sessions | Revoke all sessions for the current actor, including current | Revokes all |
| POST /api/v1/admin/staff | SuperAdmin creates Staff account and sets initial password | No |
| PUT /api/v1/admin/staff/{staffUserId}/password | SuperAdmin resets Staff password | No |
| POST /api/v1/admin/staff/{staffUserId}/mfa/enrollment | SuperAdmin initiates/resets MFA enrollment; returns invitation once | No |
| POST /logout | Revoke current session and clear cookies | Revokes |
| GET /sessions | List the current actor's own sessions | No |
| DELETE /sessions/{sessionId} | Revoke an owned non-current session | No |
| DELETE /staff/{staffUserId}/sessions/{sessionId} | Security-admin revocation of a Staff session | No |

The self-session routes are actor-generic: the authenticated Customer or Staff actor can list only their own sessions and revoke only their own non-current sessions. The response identifies the current session. Neither actor can revoke the current session through the single-session route; use logout or sign-out-all-devices for that. DELETE /sessions is the explicit sign-out-all-devices action: it atomically revokes every session owned by the current actor, including current, is idempotent, and returns 204 whether or not other active sessions existed. It clears the current browser's access, refresh, Staff-MFA and Staff-enrollment cookies. It requires an authenticated Customer or Staff session, antiforgery token and exact trusted Origin. Staff self-service sign-out-all follows the same rule.

The SuperAdmin provisioning routes require the dedicated StaffProvisioning permission and completed MFA; they are never exposed to Customer or ordinary Staff roles. The staff-to-staff route requires a dedicated SecurityAdmin permission, Staff AccountType and completed MFA. It verifies that the target session belongs to the target Staff user, prevents cross-account IDOR, records actor/target/session/reason in the audit log and performs revocation atomically. It does not permit a Customer actor or ordinary Staff role to revoke another Staff member's sessions.

Every unsafe cookie-authenticated request requires the approved antiforgery request token and exact trusted Origin validation. Login and OTP-request endpoints remain subject to exact Origin/CORS policy and endpoint-specific abuse controls.

### Customer password

Request contains identifier and password. The identifier may be email or one of the supported mobile formats. Do not accept accountType, userId, role, session lifetime, authentication method or verification flags from the client.

Success creates a session and sets cookies; JSON contains only a minimal safe account result and no raw token. Failure is generic for unknown identifier, wrong password, unconfirmed identifier, disabled/locked account or account-type mismatch.

### Customer OTP

OTP request accepts mobileNumber. Its public response is generic for every outcome. Apply phone/IP/global quotas and risk controls; only a verified Customer mobile receives a login-purpose challenge.

OTP verification accepts mobileNumber and code. The server resolves the canonical mobile and active Customer-login challenge. The challenge is purpose-bound, time-bounded, attempt-limited, single-use and atomic. Concurrent submissions cannot create multiple sessions from one challenge.

### Staff password and MFA

Staff login accepts username and password only when the account is enabled, MFA enrollment is Active and no MFA lockout is in effect. Success creates a single-use MFA transaction and sets only the dedicated MFA cookie. An account in EnrollmentPending must use the SuperAdmin-issued enrollment invitation instead; it cannot obtain an MFA transaction or normal session. Staff login does not authenticate the browser or issue normal session credentials.

MFA verification accepts either a TOTP code or a recovery code. The server resolves the pending transaction from the dedicated cookie; never accept client-supplied user ID or transaction owner. Validate purpose, expiry, cancellation/consumption state and transaction/account-level failure budgets. If the Staff account is not MFA-enrolled or is temporarily MFA-locked, reject without creating a session. After a successful factor, if `MustChangePassword=true`, atomically consume the MFA transaction/factor and issue only a restricted `__Host-staff-password-change` cookie (maximum 10 minutes); do not create AuthSession, access JWT or refresh token. This cookie authorizes only POST /staff/password/change. The endpoint accepts only the new password in its body; the restricted cookie is the required post-MFA proof. The server checks that the new password differs from the current temporary password, applies Identity password policy, clears `MustChangePassword`, and creates the normal session plus initial refresh token in the same PostgreSQL transaction. If `MustChangePassword=false`, successful MFA follows the normal shared session-creation path.

## 5. Shared session creation and transaction boundary

### Account activation state

Add a non-null Boolean `is_active` field to the Prisma user model/table in the `identity` schema. It is a server-controlled account lifecycle state, independent of the application's temporary lockout state and independent of `MustChangePassword`. Existing Customer and Staff rows are backfilled to `true` to preserve their current ability to authenticate; the migration must not silently disable existing accounts. Every new-account provisioning path must explicitly choose `IsActive` on the server. Do not accept it from public authentication/profile DTOs, and do not add a database default that hides missing provisioning decisions.

`IsActive=false` means the account cannot start or complete an authentication ceremony, obtain a session, or rotate a refresh token. OTP/MFA pending transactions for the account must not be usable. A privileged, audited account-deactivation operation must atomically mark the account inactive, invalidate its pending authentication transactions, revoke all active sessions and refresh credentials, and commit as one PostgreSQL transaction. Reactivation is also a privileged, audited state change; it does not restore previously revoked sessions or credentials. Already-issued stateless access JWTs can remain valid only for the bounded residual lifetime accepted by AR-SEC-001 (maximum five minutes plus configured clock skew); do not claim immediate JWT revocation. Add migration, authorization, refresh, pending-transaction, session-revocation and concurrent state-transition tests.

To make activation changes race-safe, use one database lock order across account-sensitive authentication operations: lock the owning `ApplicationUser` row first; then lock the relevant OTP challenge or Staff MFA transaction; then lock affected `AuthSession` rows in stable UUID order; finally lock their `RefreshToken` rows. Session creation and refresh must re-check `IsActive` while holding the user-row lock. Account deactivation takes the same order and cannot commit while a concurrent refresh/authentication operation can later create credentials for the now-inactive account. This intentionally extends the existing session-first/token-second order to user-first/session-second/token-third; update `RefreshTokenRotationService` accordingly in Foundation. No path may lock a child credential row and then acquire its owning user row.

### Staff temporary-password persistence

Add a non-null Boolean `MustChangePassword` column to `identity.AspNetUsers`, server-controlled and never accepted from public DTOs. New Customer accounts use false. SuperAdmin Staff creation and every SuperAdmin password reset set it true in the same transaction as the password update. The migration must backfill existing Staff rows to true so no pre-existing Staff account bypasses the mandatory rotation; existing Customer rows remain false. Only the successful restricted password-change ceremony may clear it. Add PostgreSQL migration/integration tests for nullability, backfill by AccountType, and rejection of client attempts to mutate the flag.

All completed ceremonies use one server-side session-creation service. It accepts only trusted server-side user ID, persisted AccountType, server-selected AuthenticationMethod, validated lifetime policy, database time and privacy-reviewed device metadata.

Authentication-method mapping:
- Customer password via email or mobile -> Password.
- Customer SMS OTP -> SmsOtp.
- Staff password + TOTP -> PasswordTotp.
- Staff password + recovery code -> PasswordRecoveryCode.

### Required atomicity

For Customer OTP and Staff MFA completion, the following state changes MUST commit or roll back together in one PostgreSQL transaction using the same Prisma transaction client/connection:

1. Lock and revalidate the OTP challenge or Staff MFA transaction.
2. For Staff TOTP, atomically advance the authenticator's last accepted time-step; for recovery, atomically consume the selected recovery code.
3. Consume the OTP challenge or Staff MFA transaction.
4. Revalidate account enabled state, expected AccountType and current authorization state.
5. For Customer OTP and Staff MFA where `MustChangePassword=false`, insert exactly one AuthSession.
6. For those completed-session flows, insert exactly one initial opaque 256-bit RefreshToken, persisting only SHA-256(secret). For Staff MFA where `MustChangePassword=true`, instead insert only a hashed, single-use, maximum-10-minute password-change transaction; do not insert AuthSession or RefreshToken.

No one-time factor may be consumed without its terminal authentication state being committed. For a Staff account with `MustChangePassword=false`, MFA factor consumption, MFA transaction consumption, AuthSession and initial RefreshToken commit together. For `MustChangePassword=true`, factor/MFA transaction consumption and insertion of a hashed, single-use, maximum-10-minute password-change transaction commit together, with no AuthSession or RefreshToken. The subsequent password-change request atomically consumes that restricted transaction, updates the Identity password, clears `MustChangePassword`, inserts AuthSession and initial RefreshToken, and signs the access JWT before commit. A failed operation rolls back all corresponding state. Password-based Customer session creation uses the same session/refresh transaction but has no one-time challenge to consume.

Generate session identifiers, refresh secret and token claims from server-side state. Access JWT signing must complete before the database transaction commits so signing failure rolls back factor consumption and session persistence. Signing in this transaction must be a bounded local signing operation; do not perform an unbounded remote network call while holding database locks. Cookie serialization/options must be validated before commit. Cookies are appended only after a successful commit. If commit fails, no credentials are returned.

If the database commit succeeds but the HTTP response is lost before the browser receives cookies, the resulting session is unreachable to that browser. Do not weaken strict refresh replay or add a retry grace period. The orphan remains bounded by the session's initial idle expiry (Customer: at most seven days; Staff: at most thirty minutes) and absolute expiry; a cleanup job must revoke/expire sessions that have passed idle/absolute expiry and remove expired refresh credentials according to retention policy. The user can authenticate again, creating a separate session. Do not attempt to infer successful cookie delivery from server-side response completion.

### Session policy

- Customer: 30-day absolute / 7-day idle.
- Staff: 12-hour absolute / 30-minute idle.
- Absolute expiry never slides; idle expiry extends only after successful refresh.
- Use UTC/database time and enforce IdleExpiresAt <= AbsoluteExpiresAt.
- Revalidate `ApplicationUser.IsActive == true` at session creation and refresh; never infer activation from lockout state or client input.
- Load current roles from server-side Identity data; never copy roles or assurance from client/JWT state.
- Every access JWT contains the server-issued `account_type` claim with exactly one lowercase value: `customer` or `staff`, derived from the persisted `AccountType`. The claim is distinct from role claims; it is never client-supplied and is not a role. Configure NestJS guards/policies to require the correct account-type claim as well as the required role/policy for protected endpoints.
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
- Use a cryptographically secure random TOTP secret; encrypt it at rest using the application-owned versioned authenticated-encryption boundary; key custody is tracked by TD-MIG-003. Never return it after enrollment or log it.
- Enrollment remains pending until the first generated code is successfully verified.

Persist LastAcceptedTimeStep per authenticator. A submitted code is valid only if its matching step is greater than LastAcceptedTimeStep. If more than one candidate step matches, select the greatest matching step that is still within the allowed window and greater than the persisted value. Advance the value with a conditional database update or row lock in the same transaction as MFA transaction consumption and session creation. A concurrent duplicate submission must have at most one winner. A code from an already accepted step is rejected even while it remains inside the clock-skew window.

If a future implementation changes algorithm/digits/period, it is a new reviewed interoperability decision and must update authenticator enrollment metadata and URI parameters; do not silently change this profile.

### MFA transaction

- Lifetime is server-configured but never greater than five minutes.
- Pre-authentication secret is cryptographically random, opaque and high entropy; persist only a hash.
- Cookie is __Host-staff-mfa; production attributes are Secure, HttpOnly, SameSite=Lax, Path=/, no Domain, maximum five-minute lifetime.
- Transaction is bound to one Staff user and one fixed purpose.
- It can only complete/cancel that transaction; it cannot be upgraded without successful MFA.
- A transaction permits at most five failed factor submissions. The fifth failure consumes the transaction and clears its cookie.
- Across all transactions/devices, maintain a database-backed rolling 15-minute Staff MFA failure counter. The fifth failed MFA verification in that window sets a 15-minute authentication lockout (MfaLockoutEnd); this is a temporary auth lock, not a change to the administrator-controlled account IsActive flag. Atomically invalidate all pending MFA transactions, revoke all active Staff sessions and refresh credentials, and reject new Staff password/MFA and refresh operations while locked. Already-issued stateless access JWTs may remain valid for at most their remaining five-minute lifetime plus configured clock skew, consistent with AR-SEC-001.
- A successful MFA completion resets the failure window/counter. When lockout expires, the counter/window resets automatically.
- The fifth-failure lockout, pending-transaction invalidation and active session/refresh revocation commit atomically in PostgreSQL; emit a security audit event. Existing access JWT residual validity remains bounded by AR-SEC-001. An explicit SuperAdmin unlock, if later implemented, must be separately permissioned and audited.
- Failure counting and lockout transition are atomic under concurrent verification; opening a new MFA transaction does not reset the account-level counter.
- Password success does not reset or bypass MFA attempt policy.

### Recovery codes

- Generate each code from at least 128 bits of CSPRNG entropy (16 random bytes).
- Encode as unpadded RFC 4648 Base32 using uppercase A-Z and 2-7; display once only. UI may group characters with hyphens for readability.
- Normalize by uppercasing and removing only the documented grouping hyphens; reject any other character or malformed length.
- Persist only SHA-256 over this exact UTF-8 byte sequence: ASCII label staff-recovery-v1, one 0x00 byte, the owning user UUID in lowercase 32-character N format, one 0x00 byte, then the normalized Base32 code. No plaintext or reversible encryption is stored. Given at least 128 bits of entropy, a password KDF is not required for offline guessing resistance; retain constant-time verification/consumption behavior.
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
| Staff MFA enrollment transaction | __Host-staff-enrollment |
| Staff post-MFA password-change transaction | __Host-staff-password-change |

All production cookies use Secure, HttpOnly, SameSite=Lax and Path=/. Their expiry is explicit and never exceeds the underlying credential/transaction expiry. The __Host- prefix is valid because Secure is set, Path is exactly /, and Domain is omitted. Enrollment and post-MFA password-change cookies are restricted, short-lived credentials and grant no normal session authority. Access and refresh remain separate cookies.

Production cookie names are `__Host-access-token`, `__Host-refresh-token`, `__Host-staff-mfa`, `__Host-staff-enrollment` and `__Host-staff-password-change`. Development over local HTTP uses separate names: `dev-access-token`, `dev-refresh-token`, `dev-staff-mfa`, `dev-staff-enrollment` and `dev-staff-password-change`. These names are permitted only in the Development environment and only for the local HTTP origin. Secure=false is allowed only there. Production/Staging must reject development cookie configuration and must never emit dev-prefixed cookies. Production must never fall back to non-Secure cookies.

Cookie lifecycle:
- Successful Customer password or OTP login: set access + refresh; clear any stale staff-MFA and staff-enrollment cookies.
- Successful Staff MFA when `MustChangePassword=false`: set access + refresh; clear staff-MFA, staff-enrollment and staff-password-change cookies.
- Successful Staff MFA when `MustChangePassword=true`: set only staff-password-change; clear staff-MFA and staff-enrollment; do not create a session or normal auth cookies.
- Successful Staff password change: clear staff-password-change and set access + refresh only after the password update and session creation commit atomically.
- Successful Staff password pre-authentication: set/replace only staff-MFA; do not set or clear normal session cookies.
- Successful enrollment invitation redemption: set only staff-enrollment; never set normal auth or staff-MFA cookies.
- Enrollment completion, cancellation, expiry or failure: clear staff-enrollment; completion does not create a session.
- Password-change transaction success, cancellation, expiry or failure: clear staff-password-change; never issue a session unless password update and session creation both commit.
- Successful Staff password reset: set `MustChangePassword=true`, revoke target sessions and pending MFA transactions; the target must still complete the existing enrolled factor unless SuperAdmin explicitly performs the separate MFA-reset ceremony.
- Failed password login or failed OTP request/verification: do not clear an unrelated existing authenticated session cookie. Keep the MFA cookie only while its transaction remains valid and attempts remain.
- Expired, consumed, cancelled or attempt-exhausted MFA transaction: clear staff-MFA cookie.
- Refresh success: rotate refresh and replace access cookie; refresh failure due to invalid/expired/revoked/replayed credentials clears access and refresh cookies.
- Logout: revoke current session and clear access, refresh, staff-MFA and staff-enrollment cookies.
- Sign-out-all-devices clears current browser's access, refresh, staff-MFA and staff-enrollment cookies after revoking all applicable sessions.
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
- Provider concurrency/spend budget, provider-specific circuit breakers, primary-to-secondary failover and bounded outbox retries.
- PostgreSQL transactional outbox, encrypted short-lived payload, challenge-aligned TTL, expiry-aware workers and terminal-row cleanup.
- Generic public responses; never log raw OTP, full phone, credentials, cookies or tokens.

OTP purpose in this slice is specifically Customer login. Registration/phone-verification purposes are separate flows and must not be accepted by the login verifier.

### SMS delivery queue, provider failover and expiry

OTP delivery uses a PostgreSQL-backed transactional outbox owned by the Identity module for this slice. Do not introduce an in-memory-only queue, require Redis, or add a message broker before measured need.

Creating a Customer OTP challenge, consuming the applicable phone/global quota and inserting its SMS outbox record happen in the same PostgreSQL transaction. The outbox row contains the challenge ID, destination metadata, protected payload, expiry, state, attempt count, next-attempt time and a bounded worker lease. Workers claim rows with database locking/lease semantics (for example, FOR UPDATE SKIP LOCKED) so multiple API/worker instances cannot send the same row concurrently.

The raw OTP is never stored in plaintext. The challenge table stores only its HMAC. The outbox temporarily stores the SMS destination and message body only as authenticated ciphertext protected with the application-owned versioned authenticated-encryption boundary; production key custody and sharing are governed by TD-MIG-003 and TD-SEC-001. The worker decrypts only in memory immediately before provider submission. The protected payload is erased as soon as delivery reaches a terminal state or the challenge expires; it is never logged, traced or returned to an API. Delivery metadata may be retained for a short operational period, then terminal outbox rows are purged. Expired pending rows are purged without sending.

The outbox expiry is exactly the OTP challenge expiry (initially two minutes). A worker MUST check database time immediately before every provider call and MUST NOT send a message whose challenge has expired, been consumed, or been invalidated by resend. A resend invalidates the previous challenge and its pending outbox work, then creates a new challenge and outbox row atomically. This prevents stale codes from being delivered after a newer challenge exists.

Configure an ordered primary and secondary SMS provider through provider-independent adapters. The worker tries the primary first and fails over to the secondary on provider unavailability, network/timeout errors, HTTP 429, HTTP 5xx or an open circuit. Provider authentication/configuration failures also open that provider's circuit and alert operators; they must not block use of a healthy secondary. Permanent message-level failures (for example, invalid destination or rejected template/content) are terminal and do not loop through providers. Credentials are held outside source control and the database.

Use a bounded retry policy: at most three total submission attempts per outbox message, with short bounded backoff (initial defaults: two seconds, then five seconds); fail over to the secondary immediately when the primary has a transient/provider-level failure. Circuit breakers are provider-specific and prevent repeatedly calling an unhealthy provider. If a provider supports idempotency, pass the stable outbox/challenge ID as the idempotency key. Retries always send the same challenge and same OTP; they never generate a new code. An ambiguous timeout can result in duplicate SMS delivery across providers, but any duplicate contains the same still-valid OTP and does not create another challenge or extend its expiry.

The API returns the same generic 202 Accepted response after a challenge/outbox transaction commits, without claiming that the SMS has already been delivered. OTP verification does not require a provider delivery receipt: a valid, unexpired challenge may be verified while delivery is pending or its outcome is unknown. A definite permanent rejection invalidates the challenge. If the bounded retry budget is exhausted after transient/ambiguous failures, retain the challenge as verifiable only until its original expiry because a provider may have accepted the message despite a lost response; do not extend the TTL. The user may request a new challenge after the normal cooldown, which invalidates the previous one.

Queue cleanup is mandatory: pending rows are deleted at expiry; protected payloads are erased immediately on delivered, terminal or expired states; terminal metadata rows are purged after 24 hours by a scheduled cleanup task. No expired SMS remains eligible for delivery. The provider fallback order, retry count, backoff, circuit-breaker thresholds and cleanup interval are configuration with safe bounded defaults and operational metrics/alerts. Provider failover never bypasses per-phone/global quotas or the total spend/concurrency budget.

## 9. Failure, concurrency and authorization invariants

- Customer and Staff endpoints enforce the persisted AccountType server-side and the matching signed `account_type` JWT claim (`customer` or `staff`).
- `IsActive` is non-null and server-controlled; it is independent of `LockoutEnd` and `MustChangePassword`. Inactive accounts cannot authenticate, complete pending OTP/MFA ceremonies, create sessions or refresh tokens.
- Account deactivation atomically invalidates pending authentication transactions and revokes active sessions/refresh credentials; reactivation never restores revoked credentials. Already-issued stateless JWT validity remains bounded by AR-SEC-001.
- Customer email/mobile lookup never falls back to Staff accounts; Staff username lookup never falls back to Customer accounts.
- Unconfirmed email/mobile cannot authenticate by password; unverified mobile cannot receive or verify login OTP.
- OTP verification and Staff MFA completion are single-use under concurrency.
- Challenge/MFA consumption, recovery-code consumption where applicable, AuthSession insertion and initial RefreshToken insertion are atomic.
- A successful ceremony creates exactly one session and one initial active refresh token.
- Session creation and refresh rotation use consistent session/token lock ordering: session first, then token.
- Self-session operations are ownership-scoped for both Customer and Staff. DELETE /sessions revokes all of the current actor's sessions, including current, idempotently. Security-admin Staff-to-Staff revocation is separately permissioned and audited.
- Staff account creation, password assignment/reset and MFA enrollment/reset are SuperAdmin-only, audited and cannot bypass mandatory MFA.
- Staff MFA enrollment is invitation-bound, one-time and password-confirmed; TOTP secret/recovery codes are visible only to the employee during one-time enrollment.
- Staff MFA failures are counted across transactions/devices; the fifth failure in a rolling 15-minute window causes a 15-minute temporary authentication lockout.
- Current roles are loaded from server-side Identity data; no client role/account classification is trusted.
- Staff endpoints and Staff sessions require Staff AccountType, the required role/policy and completed MFA.
- Stateless access JWT revocation remains bounded by its approved maximum lifetime plus clock skew.

## 10. Required verification

### Identifier and account separation
- `IsActive` migration backfills existing Customer and Staff accounts to true, remains non-null, and has no implicit database default for new accounts.
- New account provisioning explicitly sets `IsActive`; public DTOs cannot set or change it.
- Inactive Customer/Staff password login, OTP request/verify, Staff MFA completion, session creation and refresh are rejected.
- Deactivation atomically invalidates pending OTP/MFA transactions and revokes all active sessions/refresh tokens; reactivation does not resurrect credentials; concurrent deactivate/reactivate operations preserve a deterministic audited final state.
- Access JWT contains exactly one lowercase `account_type` claim derived from persisted AccountType; Customer/Staff endpoint policy rejects missing, malformed or mismatched claim values independently of role claims.
- Email normalization, case-insensitive lookup and duplicate normalized Customer email rejection at PostgreSQL level.
- Iranian mobile numeral normalization and accepted/rejected input formats.
- Canonical phone uniqueness for Customer accounts, including concurrent writes.
- Exact partial-index predicates, NULL/empty-value checks, and migration failure on existing duplicates.
- AccountType conversion collision rolls back both conversion and session revocation; successful conversion revokes all sessions atomically.
- Customer rejects Staff and Staff rejects Customer; Customer username is never a login identifier.
- Only SuperAdmin can provision Staff, assign/reset passwords or initiate/reset MFA enrollment; all actions are permissioned and audited.

### Customer password and OTP
- Correct/incorrect password, disabled/locked account, unknown identifier and unconfirmed identifier.
- Generic failure response and no account enumeration.
- OTP purpose binding, expiry, resend invalidation, single use, max attempts, wrong-code behavior and concurrent verification.
- OTP challenge consumption + session + initial refresh insertion rollback together on forced failure.
- Concurrent duplicate OTP verification creates at most one session.
- Quotas under concurrent requests and multiple API instances; outbox insertion atomicity; worker lease/concurrency; primary-to-secondary failover; transient/permanent/ambiguous provider outcomes; bounded retry; idempotency where supported; expired-message suppression; encrypted-payload deletion; terminal-row cleanup; provider spend/concurrency limits.
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
- Staff enrollment invitation is single-use, hash-only at rest, time-bounded, requires the administrator-provided password, and cannot create a session.
- TOTP secret is returned only once to the employee, never to SuperAdmin; first valid code activates MFA and recovery codes are displayed once.
- MFA reset invalidates old authenticators/recovery codes and all sessions before reenrollment; Staff cannot log in while enrollment is pending.
- Five failed MFA submissions in a rolling 15-minute account window trigger a 15-minute lockout across new transactions/devices; the transition atomically revokes active sessions/refresh credentials, invalidates pending MFA transactions and is audited. Already-issued JWT residual validity is bounded by AR-SEC-001; concurrency cannot exceed the threshold.
- Staff password reset sets `MustChangePassword=true`, revokes active sessions and pending MFA transactions without silently bypassing or resetting TOTP.
- SuperAdmin Staff creation and every password reset persist `MustChangePassword=true`; existing Staff rows are backfilled to true by migration and Customer rows to false.
- After successful MFA with `MustChangePassword=true`, only the restricted password-change cookie is issued; no AuthSession, access JWT or refresh token exists.
- The restricted cookie cannot access business APIs, session-management routes or refresh; it is single-use, expires within 10 minutes and is cleared on all terminal outcomes.
- The password-change endpoint rejects reuse of the temporary password, enforces Identity password policy, and atomically consumes the restricted transaction, updates the password, clears the flag and creates the session/initial refresh token.
- Forced failures at each password-change transaction step roll back the password/flag/session changes together; concurrent submissions produce at most one successful session.
- Staff with `MustChangePassword=true` can complete password + MFA but cannot obtain a normal session until the temporary password is changed.
- The post-MFA password-change credential permits only the password-change endpoint and expires within 10 minutes; password update, flag clear, session and initial refresh insertion are atomic.

### Session, cookie, browser and authorization
- Correct Customer/Staff lifetime selection and absolute-expiry cap.
- Exactly one initial refresh token per new session; hash-only persistence.
- RS256, key ID, issuer/audience/lifetime validation and five-minute maximum.
- Production and Development cookie names/attributes; production never emits dev names or insecure cookies.
- Cookie clearing behavior on refresh failure, logout, MFA completion and terminal MFA failure.
- Lost-response orphan session remains bounded by initial idle expiry and cleanup.
- CSRF token, missing/hostile Origin and exact CORS allow-list.
- Refresh concurrency/replay, logout and session revocation regression tests.
- Customer and Staff self-session ownership, current-session protection, DELETE /sessions all-device revocation (including current session), idempotency, cookie clearing, CSRF/Origin and audit coverage; SecurityAdmin cross-staff revocation remains separately permissioned.
- Role matrix, horizontal/vertical escalation and IDOR.
- Build, unit tests and PostgreSQL-backed integration tests pass in CI.

## 11. Implementation sequence and gates

1. **2C-Design (this document):** approve endpoint, normalization, persistence and ceremony contracts before implementation.
2. **2C-Foundation:** non-null server-controlled `IsActive` state and migration/backfill; shared validated session policy and session-creation service; RS256 Access JWT issuance with the signed `account_type` claim and role claims loaded from persisted Identity data; initial refresh token + access-token issuance boundary; NestJS authentication guards and authorization claim mapping; unit/PostgreSQL tests.
3. **2C-Customer:** email/mobile password login and verified-mobile OTP request/verify; PostgreSQL transactional SMS outbox, primary/secondary provider failover, expiry-aware retries and uniqueness/anti-enumeration tests.
4. **2C-Staff:** SuperAdmin provisioning/password management; one-time employee MFA enrollment/reset; password pre-authentication transaction, TOTP verification, replay prevention, account-level MFA lockout and recovery-code consumption.
5. **2C-Completion:** cookie/CSRF/Origin/CORS integration, authorization policies, Customer/Staff session management including DELETE /sessions all-device revocation and end-to-end tests.
6. **2C-Security review:** inspect full diff, threat model, concurrency coverage, CI and debt register; no merge without explicit owner approval.

Each implementation slice is a separate reviewable PR or an explicitly agreed small set of PRs. Every PR updates progress and any technical-debt rows it introduces. Do not begin the next slice until the current slice's tests and review are complete and the owner approves proceeding.

## 12. Explicit non-goals and tracked production gates

No new deferral is created merely by documenting a provider-independent boundary. The initial SMS outbox is PostgreSQL-backed; Redis or a broker is not a prerequisite. Existing gates remain authoritative:
- TD-MIG-003 / TD-SEC-001: production application authenticated-encryption key custody and protection.
- TD-SEC-002: JWT signing-key custody and runtime integration.
- TD-SEC-003: OTP HMAC secret provisioning/rotation.
- TD-SEC-004: SMS provider/sender/contract.
- TD-SEC-005: production origins/TLS/cookie configuration.
- TD-SEC-006: WAF/CDN/DDoS/trusted proxy.
- TD-SEC-007: distributed rate limiting/Redis decision.
- TD-OPS-002: privacy retention/deletion.

Provider selection alone does not close any gate. Production must remain fail-closed where the security design requires it.
