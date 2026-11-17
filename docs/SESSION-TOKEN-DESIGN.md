# Session and Token Lifecycle Design

**Status:** Approved design baseline. PR #7 merged the approved lifecycle/security decisions. Slice 2B is implementing the persistence model and database-critical refresh rotation; HTTP endpoints, JWT issuance and cookie integration remain out of scope for this slice.

## 1. Scope and non-goals

This document defines browser session boundaries, access JWT issuance, opaque refresh-token rotation, replay handling, staff MFA pre-authentication state, logout/revocation, and the database invariants needed to make these operations concurrency-safe.

The browser deployment topology is now decided: the customer storefront is `https://shop.com`, the admin panel is `https://panel.shop.com`, and the API is `https://api.shop.com`. The storefront and admin panel call the API cross-origin with credentials, but all three are same-site under the same HTTPS registrable domain. This document records the resulting browser security boundary. It also defines the JWT signing algorithm and key lifecycle without selecting a specific secret manager, KMS or HSM product. SMS vendor and immediate access-token revocation mechanism remain outside this decision.

The API is browser-facing and uses separate HttpOnly cookies for access and refresh credentials. Raw token values must never appear in JSON, URLs, browser storage, application logs, traces, or exception messages.

## 2. Core decisions proposed

1. **One session per authenticated browser/device.** A successful authentication creates an `AuthSession`; another device or browser gets a different session.
2. **One refresh-token family per session.** A session has a linear chain of refresh-token records. At most one token in that chain may be active at a time.
3. **Short-lived access JWT.** Initial target and maximum: 5 minutes. Server-side configuration may lower this limit but must not raise it above 5 minutes without a separately reviewed security decision. It is not refreshed in place; refresh issues a new access JWT.
4. **Opaque refresh credential.** Generate a 256-bit cryptographically random secret. The browser receives an opaque value containing a public token identifier and the secret; the database stores only SHA-256(secret), never the secret.
5. **Single-use rotation.** Every successful refresh consumes the presented token and creates exactly one successor in the same database transaction.
6. **Strict replay response.** A valid, already-consumed refresh token is evidence of replay: revoke that session and its active refresh token. A token-ID match with a secret-hash mismatch is just an invalid credential and must not revoke the session.
7. **Serialize refresh on the client.** The frontend must use a single-flight refresh operation so concurrent requests/tabs do not intentionally submit the same token simultaneously. Use cross-tab coordination where supported (for example, Web Locks or BroadcastChannel), but treat it as reliability coordination, not a security boundary. Server-side conditional updates remain authoritative.
8. **Strict replay is accepted despite its availability trade-off.** Any second presentation of a valid, already-consumed refresh token revokes that session, including a legitimate retry after a lost response or a race that escaped client coordination. No grace period or idempotent replay window is provided. The affected user may need to authenticate again.
9. **JWT-claim authorization is the selected initial strategy.** The API authorizes requests using server-issued role claims in the short-lived access JWT; it does not query the Identity database on every authorized request. Authorization changes therefore become effective when the current access JWT expires (target maximum 5 minutes), unless the session is separately revoked and an immediate-revocation mechanism is later introduced. This bounded staleness is an explicit accepted trade-off.
10. **Staff MFA is a separate pre-authentication transaction.** Password success for staff does not create a normal session or access/refresh cookies. A short-lived, purpose-bound MFA transaction is required until TOTP or a single-use recovery code succeeds.
11. **Customer and staff assurance are explicit.** Session metadata records the authentication method/assurance that created it. Staff sessions can only be created after successful MFA.
12. **Account type is explicit and server-controlled.** Each Identity user has exactly one account type (`Customer` or `Staff`), independent of roles. Customer authentication endpoints reject Staff accounts; Staff authentication endpoints reject Customer accounts. Account type is assigned only by trusted provisioning/administrative workflows, never by client input. Conversion between account types is a privileged, audited operation and must revoke existing sessions.

## 3. Conceptual persistence model

All identifiers are application-generated UUIDv7. Exact EF property types, indexes and migrations will be specified in implementation design.

`ApplicationUser` must have a required, non-nullable, server-controlled `AccountType` with exactly two initial values: `Customer` and `Staff`. Do not silently default newly provisioned users to either type; trusted provisioning flows must set it explicitly. Enforce the allowed values in the persistence model/database where practical. It is independent of `ApplicationRole` and role assignments. Account-type conversion is privileged, audited, and revokes all existing sessions.

### AuthSession

One row per browser/device session.

| Field | Purpose |
|---|---|
| Id | UUIDv7 session identifier; also the access JWT `sid` claim |
| UserId | Identity user that owns the session |
| ActorKind | Customer or Staff classification used by authentication policy |
| AuthenticationMethod | Password, SmsOtp, PasswordTotp, or PasswordRecoveryCode |
| AuthenticatedAt | Time the authentication ceremony completed |
| CreatedAt | Session creation time |
| LastSeenAt | Updated on successful refresh, not every API request |
| AbsoluteExpiresAt | Hard upper bound; cannot be extended by refresh |
| IdleExpiresAt | Sliding inactivity boundary, extended only by successful refresh |
| RevokedAt | Null while session is not revoked |
| RevocationReason | Controlled internal reason code; never client-supplied |
| UserAgentHash / DeviceLabel | Optional, privacy-reviewed session display metadata; never trusted for authentication |

A session is refreshable only when it is not revoked and both absolute and idle expiry checks pass. Use UTC instants. Do not treat an IP address or User-Agent as a secret or as proof of device identity.

### Session management UX and device metadata (approved)

- Authenticated customers may list only their own active sessions and see which one is the current session.
- The customer session list may show a coarse device/browser label, session creation time, last successful refresh time, and authentication method. Labels are informational hints derived from User-Agent data; they are not device identity or an authentication factor.
- Do not display IP addresses in the initial customer session UI. Do not use IP address or raw User-Agent as proof of identity. Avoid retaining raw User-Agent longer than operationally necessary; persist only a normalized display label or a privacy-reviewed representation.
- A customer may revoke any of their other sessions while preserving the current session. Provide a separate, explicit “sign out all devices” action that revokes every session, including the current one. Both operations must be authenticated, CSRF-protected, ownership-scoped, atomic/idempotent as appropriate, and audited.
- Staff session revocation by another staff member is restricted to an explicit security-administration permission, is ownership/target-scoped by server policy, and must emit an audit event. Staff must also be able to revoke their own other sessions; “sign out all devices” follows the same explicit all-session semantics.
- Session metadata is for user awareness and support only. It must not silently become a risk score, authorization input, or security factor without a separate reviewed design.

### Initial session lifetime policy

The following defaults are approved for the initial implementation and must remain configurable through validated server-side configuration:

| Actor | Absolute lifetime | Idle lifetime |
|---|---:|---:|
| Customer | 30 days | 7 days |
| Staff | 12 hours | 30 minutes |

Absolute expiry is a hard ceiling and must never slide on refresh. Idle expiry may be extended only by a successful refresh, and must never exceed the absolute expiry. These values are initial policy defaults, not permanent product constraints; changing them later must be an explicit configuration/policy change with tests.

### RefreshToken

One row per token in a session's rotation chain.

| Field | Purpose |
|---|---|
| Id | UUIDv7 public token identifier |
| SessionId | Owning AuthSession / token family |
| SecretHash | SHA-256 hash of the 256-bit random secret; unique |
| CreatedAt | Issuance time |
| ExpiresAt | Token expiry, never later than session absolute expiry |
| ConsumedAt | Set once by successful rotation |
| RevokedAt | Set when session/family is revoked |
| ReplacedByTokenId | Successor token ID after successful rotation |

The browser token format is versioned and opaque, conceptually `v1.<token-id>.<base64url-secret>`. The token ID is only a lookup key, not an authenticator. Compare the stored hash with a constant-time comparison after strict format/length validation. Never log the presented value.

Required database invariants:
- Unique token ID and unique secret hash.
- A foreign key from token to session.
- At most one unconsumed, unrevoked refresh token per session (PostgreSQL partial unique index).
- A consumed token has a consumption timestamp and, on successful rotation, a successor reference.
- Every refresh token's `ExpiresAt` must be less than or equal to both its owning session's `AbsoluteExpiresAt` and current `IdleExpiresAt` at issuance. If a separate refresh-token lifetime is introduced later, the effective expiry is the earliest of that lifetime and both session boundaries.
- On successful refresh, calculate the new idle expiry as `min(database_now + configured_idle_window, AbsoluteExpiresAt)`. The successor token expiry must not exceed the newly calculated idle expiry or the session absolute expiry.
- Rotation/revocation transitions are conditional and atomic; application-level read-then-write checks alone are insufficient.

### StaffMfaTransaction

Short-lived state after staff password validation and before MFA completion.

| Field | Purpose |
|---|---|
| Id | UUIDv7 transaction identifier |
| UserId | Staff Identity user |
| SecretHash | Hash of a cryptographically random pre-authentication secret |
| Purpose | Fixed value such as StaffSignInMfa; cannot be changed by the client |
| CreatedAt / ExpiresAt | Short lifetime; proposed default 5 minutes |
| FailedAttempts | Bounded TOTP/recovery verification attempts |
| ConsumedAt | Single-use completion marker |
| CancelledAt | Optional invalidation marker |

The pre-authentication secret is delivered only in a separate HttpOnly cookie scoped as narrowly as practical to the MFA flow. It is not an access token, refresh token, or authenticated session. A successful TOTP/recovery verification atomically consumes this transaction and creates the normal AuthSession. Password-only staff login must never set normal session cookies.

## 4. Access JWT

### Claims

Keep claims minimal:
- `sub`: Identity user ID.
- `sid`: AuthSession ID.
- `jti`: unique JWT ID.
- `iss`, `aud`, `iat`, `nbf`, `exp`.
- `amr` or an equivalent controlled claim describing the completed authentication method/assurance, where required by policy.

Do not accept client-supplied roles, permissions, user IDs or assurance values. Do not put PII, phone numbers, addresses or other unnecessary data in the JWT.

### Authorization model (approved: JWT claims)

Authorization is based on claims minted by the API after successful authentication. The client cannot submit or override roles, permissions, account type, or authentication assurance. The initial implementation will place server-authoritative role claims in the access JWT. NestJS authorization guards/policies map policy requirements to those role claims; do not duplicate an expansive permission catalog into every token. API authorization policies evaluate the signed claims without a per-request Identity database lookup. If later requirements introduce user-specific permission grants that cannot be represented by role claims, that is a separate design change.

This deliberately accepts bounded authorization staleness: a role-assignment change does not alter an already-issued JWT. With the approved 5-minute access-token lifetime, the normal maximum stale-role window is the remaining lifetime of that JWT, plus only the explicitly configured JWT clock-skew allowance. Refresh must issue claims from current server-side account/role data, not copy claims from the previous token. Do not lengthen access-token lifetime to improve performance without revisiting this decision.

Revoking a refresh session alone does not invalidate an already-issued stateless access JWT. The same residual window applies after role removal or account disablement unless a future immediate-revocation mechanism (such as a session/version check) is explicitly designed and implemented. This design does not claim immediate revocation. Sensitive operations remain subject to their explicit policy requirements, audit logging and other domain-level safeguards; these do not silently imply a database permission lookup on every request.

### Validation

Validate signature, a fixed allow-list containing only the configured `RS256` algorithm, issuer, audience, lifetime, not-before and key ID (`kid`). Reject unsigned tokens, algorithm confusion, unexpected algorithms and unknown keys. The API must not accept an algorithm selected dynamically from an untrusted token header.

### Approved signing-key strategy

- Use asymmetric RSA signing with **RS256**. The initial RSA private key must be at least 3072 bits.
- The private signing key is available only to the API signing boundary. JWT validation uses the corresponding public key; public keys may be distributed to trusted internal consumers without exposing signing capability.
- Keep private keys outside source control, application configuration files, database records and container images. Production deployment must obtain signing capability from an access-controlled secret manager or an appropriate KMS/HSM-backed integration. The exact provider is a deployment decision, not part of this design.
- Give every signing key a unique, non-secret `kid`. The API signs new tokens with exactly one configured active key and validates tokens against the active and explicitly retained previous public keys.
- During rotation, publish/load the new public key before signing with the new private key. Keep the previous public key available for at least the maximum access-token lifetime plus the configured clock-skew allowance after the last token was signed with it; then retire it. Unknown `kid` values must be rejected.
- Define named ownership for key creation, activation, rotation, emergency compromise response and recovery in deployment/runbook documentation. Key backup or recovery material must be protected independently; a compromised private key must be retired, not restored as trusted.
- In Production, startup must fail closed if no valid active signing key and corresponding verification configuration are available. Do not generate an ephemeral replacement key automatically, because that can invalidate tokens unpredictably and conceal deployment errors.
- Local development may use a developer-local key or .NET User Secrets. Development keys must never be reused in staging or production.
- Do not reuse the application authenticated-encryption key as the JWT signing-key store. These are separate cryptographic purposes and lifecycles.

The API extracts the access JWT from the configured access cookie. Do not also accept it from query strings. Whether non-browser bearer clients are supported is a separate API-client decision and must not weaken browser CSRF policy.

## 5. Refresh rotation transaction

Refresh is a state-changing, cookie-authenticated operation. It requires CSRF validation and trusted Origin checks.

Proposed server algorithm (PostgreSQL concurrency contract):

1. Parse the versioned token format and validate lengths/encoding before database work. Look up the token by its public token ID to obtain its immutable session ID and stored secret hash.
2. Verify the presented secret against `SecretHash` using a constant-time comparison after strict decoding. On mismatch, return a generic unauthenticated response; do not revoke the session. Do not reveal whether the token ID exists.
3. Begin a PostgreSQL transaction at explicit `READ COMMITTED` isolation. All operations that mutate a session or any token in its family (refresh, replay revocation, logout, administrative revocation and expiry cleanup) must use the same global lock order: **User → pending authentication transaction (when applicable) → AuthSession row(s) → RefreshToken row(s)**. For refresh, the concrete order is **User → AuthSession → RefreshToken**. Lock multiple session rows in ascending UUID order and multiple token rows in ascending UUID order. No operation may lock a session/token and then attempt to acquire its user row, or lock a token and then attempt to acquire its session row.
4. Lock the owning User row with `SELECT ... FOR UPDATE` and reject an inactive account. Then lock the owning AuthSession row, followed by the presented RefreshToken row, each with `SELECT ... FOR UPDATE`. Re-read the token state and stored hash and verify the secret again. If the row disappeared or the hash no longer matches, reject generically without revoking anything.
5. After acquiring all locks, evaluate session/token expiry using PostgreSQL `clock_timestamp()`, never a transaction-start timestamp or JavaScript wall clock. The service obtains a single database-clock snapshot for classification, then repeats expiry predicates with a fresh database-clock CTE in each conditional mutation so a credential cannot pass an earlier check and still be consumed after expiry. First reject a revoked session or a session whose current `IdleExpiresAt` or `AbsoluteExpiresAt` has passed. This prevents an already-ended session from being revived.
6. With the secret re-verified under lock, inspect the token state **before checking the presented token's own expiry**:
   - If `ConsumedAt` is non-null, this is a valid consumed-token replay. In the same transaction, mark the owning session revoked and revoke every still-active refresh token in that session's family. Commit the revocation and do not revoke any other session belonging to the user. A consumed token remains a replay signal even if its original token expiry has since passed, provided its owning session itself is still active and within its session lifetime.
   - If `RevokedAt` is non-null, reject and clear stale cookies without rotating.
   - If the token is not consumed or revoked but `ExpiresAt <= database_now`, reject and clear stale cookies without rotating.
   A secret mismatch is never a replay signal and must never trigger revocation.
7. Preserve the session's configured idle window as the positive interval represented by `IdleExpiresAt - LastSeenAt`. On successful rotation, calculate `newIdleExpiresAt = min(database_now + idle_window, session.AbsoluteExpiresAt)`. Reject inconsistent session state where the idle window is not positive. The absolute expiry never slides.
8. Conditionally claim the predecessor using an `UPDATE` with its own PostgreSQL clock CTE that repeats the token/session predicates: matching token and session IDs, `ConsumedAt IS NULL`, `RevokedAt IS NULL`, `ExpiresAt > database_now`, and an `EXISTS` check for an unrevoked, unexpired session. Require exactly one affected row. The database has a partial unique index allowing only one active token per session, while its check constraint requires a consumed token to reference an already-inserted successor. Therefore the transaction temporarily sets the predecessor's `RevokedAt` to free the partial unique index, updates the session, inserts exactly one successor bounded by the updated idle/absolute expiry, then finalizes the predecessor with `ConsumedAt`, `ReplacedByTokenId` and `RevokedAt = NULL`. This temporary revoked state must never be committed.
9. All predecessor claiming, session activity/idle-expiry update, successor insertion and predecessor finalization occur in the same transaction. Any failure after the temporary claim must throw and roll back the entire transaction; it must not return a normal rejection that commits the temporary state. The successor credential is generated from a 256-bit cryptographic secret, and only its SHA-256 hash is persisted.
10. The M4 service returns the successor refresh credential to its caller only after the transaction commits. JWT issuance, HTTP endpoints and Set-Cookie response handling are outside M4; a future endpoint must not emit either credential before the transaction commits.

Because both concurrent requests lock the same session row first, the second request waits. After the first commits, the second observes the consumed token and matching secret, then applies the strict replay revocation policy. The resulting behavior is deliberate: even a legitimate duplicate request or retry after a lost response can revoke that session and require re-authentication. Frontend single-flight and cross-tab coordination reduce accidental duplicates but are not security boundaries.

## 6. Session lifecycle and revocation

### Creation

- Customer password login: create session only after Identity password validation, `AccountType == Customer`, and account eligibility checks.
- Customer SMS OTP login: create session only after atomic, purpose-bound OTP verification/consumption, `AccountType == Customer`, and verified-phone eligibility checks. A Staff account must be rejected even if it has a verified phone.
- Staff login: require `AccountType == Staff`; password success creates only StaffMfaTransaction. Successful TOTP or recovery-code verification consumes the transaction and creates the session. A Customer account must not enter the staff MFA flow.
- Never allow OTP login to register an account implicitly.

### Refresh and expiry

A refresh is allowed only if the session is active, the token is active, and token/session expiry checks pass. Idle expiry may slide on successful refresh, but absolute expiry never slides. The approved initial session durations are configurable: customers 30-day absolute / 7-day idle; staff 12-hour absolute / 30-minute idle. Do not silently ship unlimited or effectively permanent sessions.

### Logout and administrative revocation

- Logout revokes the current session and all its active refresh tokens, then clears both cookies.
- Session-list/revoke operations are scoped to the authenticated user; staff administration requires an explicit permission and audit event.
- Password reset/change, account disablement, credential compromise, staff MFA reset and security-sensitive account recovery revoke relevant sessions. The exact policy (all sessions vs current session) must be explicit per event; default to all sessions for credential-compromise events.
- Revoke only the affected session on refresh-token reuse, not every session belonging to the user, unless additional compromise evidence justifies broader revocation.
- Session revocation does not immediately invalidate already-issued stateless JWTs. Initial accepted residual validity is the remaining access-token lifetime (target 5 minutes). Immediate revocation would require a per-request or cached session-version/revocation check with explicit failure behavior and performance tests.

## 7. Cookies and CSRF boundary

### Confirmed browser topology

- Customer storefront: `https://shop.com`.
- Admin panel: `https://panel.shop.com`.
- Backend API: `https://api.shop.com`.
- Both frontends call the API from a different Origin and must send credentials explicitly. The API must allow only these exact Origins: `https://shop.com` and `https://panel.shop.com`.
- All production endpoints must use HTTPS. With this topology, requests are cross-origin but same-site (schemeful site), because the hosts share the same registrable domain and scheme.

### Cookie and browser policy

- Set authentication cookies from `api.shop.com` as **host-only cookies**: omit the `Domain` attribute. Do not widen authentication cookies to `.shop.com`; neither frontend needs to read them because they are HttpOnly.
- Use `Secure`, `HttpOnly`, and explicit `SameSite=Lax` for the access and refresh cookies in this same-site HTTPS deployment. SameSite is not a substitute for CSRF protection: sibling subdomains are same-site, so unsafe requests still require anti-forgery validation and strict Origin validation.
- Prefer `__Host-`-prefixed cookie names where practical. Such cookies must use `Secure`, `Path=/`, and no `Domain`. If a narrower refresh-cookie Path is later selected, do not use the `__Host-` prefix for that cookie.
- Configure browser clients to send credentials (`credentials: include` / equivalent) when calling `api.shop.com`.
- CORS must allow exactly `https://shop.com` and `https://panel.shop.com`, with credentials enabled. Never use wildcard origins or reflect arbitrary Origin values.
- For unsafe cookie-authenticated requests, require a CSRF token and validate the request Origin against the exact allow-list. Apply this to login-related state changes, OTP request/verify, refresh, logout, session management, and all other unsafe cookie-authenticated endpoints as appropriate. Define a CSRF bootstrap/token-delivery contract before endpoint implementation.
- Clear cookies with the same name, Path, and other relevant attributes used when setting them.
- Local development must have an explicit development-only origin/cookie configuration. Do not weaken production `Secure`, CORS, or CSRF settings to make local HTTP development work.

Required properties:
- Access and refresh credentials are separate cookies, both HttpOnly.
- Secure is mandatory in production.
- Prefer host-only cookies; set Domain only when the topology proves it is required.
- Use explicit Path, expiry and SameSite values.
- Cross-site deployment requires SameSite=None; Secure and robust CSRF protection.
- Browser refresh, logout and all unsafe cookie-authenticated requests require anti-forgery validation and trusted Origin validation.
- CORS must use exact allowed origins; never combine wildcard origins with credentials.
- Clear cookies with the same Path/Domain/SameSite attributes used to set them.
- Use a separate short-lived MFA transaction cookie; do not reuse the refresh cookie for pre-authentication.

A `__Host-` cookie prefix is a strong option when the deployment can satisfy its requirements (Secure, Path=/, no Domain). Local HTTP development may require a separate development-only configuration; production security flags must not be weakened to accommodate local development.

### MFA transaction cookie and CSRF contract (approved)

#### Staff MFA transaction cookie

- After valid staff password verification, issue only a short-lived pre-authentication cookie named `__Host-staff-mfa`; do not issue access or refresh cookies at this stage.
- The cookie is host-only, `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, and has no `Domain` attribute. Its lifetime must not exceed the MFA transaction's server-side expiry (initial default: 5 minutes).
- Its value is an opaque, versioned transaction credential containing a public transaction identifier and a cryptographically random secret. Persist only a secure hash of the secret. Validate purpose, expiry, attempt limit, cancellation and single-use state on every MFA completion attempt.
- The cookie authorizes only completion/cancellation of that specific pending staff MFA transaction. It must never authorize normal API access, session listing, refresh or any other authenticated operation.
- Clear it on successful MFA completion, cancellation, expiry handling and logout/error paths where the transaction is invalidated. Cookie clearing must match its original name and attributes.
- The cookie remains host-only at `api.shop.com`; the admin frontend never reads it. The admin frontend sends credentialed requests to the API.

#### CSRF bootstrap and validation

- Use a server-issued **synchronizer-token** CSRF pattern, not a naive client-generated double-submit value. The API issues a CSRF bootstrap response from a safe endpoint, initially `GET /api/v1/security/csrf-token`; the request token is bound to the authenticated browser/session context, kept in frontend memory and sent in `X-CSRF-TOKEN`. The implementation is NestJS-specific but the security contract is framework-independent; see TD-MIG-002.
- The bootstrap response must be `Cache-Control: no-store`, must be served only to the exact configured frontend Origins, and must not create an authenticated session or grant authorization. Browser clients call it with credentials enabled and keep the returned request token in memory only.
- Every unsafe cookie-authenticated request sends the request token in `X-CSRF-TOKEN`. The API validates the request-token/antiforgery-cookie pair and independently validates that the request Origin exactly matches the allow-list (`https://shop.com`, `https://panel.shop.com` in production). Missing Origin, untrusted Origin, missing token or invalid token is rejected; do not fall back to Referer as an authorization substitute.
- Apply this to customer/staff password login, OTP request/verify, staff MFA completion/cancellation, refresh, logout, session listing/revocation when cookie-authenticated, and all other unsafe cookie-authenticated routes. Safe bootstrap/read-only requests do not mutate authentication state.
- After authentication state changes (including successful login or MFA completion), the frontend obtains a fresh antiforgery request token before subsequent unsafe requests. The server must use the framework's supported antiforgery APIs and must not invent its own cryptographic token format.
- Frontends retain request tokens in memory only; never put them in LocalStorage, SessionStorage, URLs, logs or analytics. A page reload simply obtains a fresh token.
- CSRF validation and exact Origin validation are independent, mandatory controls. SameSite=Lax and CORS do not replace either control. CORS remains exact-origin with credentials; no wildcard or arbitrary Origin reflection.

## 8. Key and secret separation

Keep distinct key material and purposes:
- JWT signing private key / verification public keys.
- OTP HMAC secret.
- TOTP secret encryption using the application-owned versioned authenticated-encryption boundary.
- Application authenticated-encryption key material for TOTP and protected outbox payloads.
- Refresh tokens and MFA transaction secrets generated with a cryptographic RNG; only their hashes persist.

Secrets and key rings must be external to source control, access-controlled, backed up and rotated with a documented recovery plan. Production encryption keys must be persistent and available to authorized API/worker instances that need to decrypt the same payloads. The exact key custody and rotation mechanism remains tracked by TD-MIG-003 and TD-SEC-001 in docs/TECHNICAL-DEBT.md; it is a production blocker, not an untracked future task.

### Application authenticated-encryption deployment policy (approved)

- Production authenticated encryption must use a versioned key set available to every authorized API/worker instance that needs to protect/unprotect the same payloads.
- Set one explicit, stable ApplicationName for this application across those instances. Keep Development, Staging and Production key rings isolated.
- Protect key-ring material at rest with an independently managed certificate or KMS-backed mechanism; restrict read/write access to the API workload and authorized operators.
- Define protected backup, restore, rotation and compromise-recovery procedures. Losing the key ring can make previously protected payloads unrecoverable; backup restoration must be rehearsed.
- Production must fail closed if persistent shared storage or at-rest protection is missing or invalid. Do not silently fall back to ephemeral or local-container storage.
- Local development may use a developer-local key ring or a Docker volume, isolated from every shared environment.
- The exact storage and encryption provider is intentionally not selected until the hosting platform is selected. This is tracked as TD-SEC-001 in docs/TECHNICAL-DEBT.md, with a mandatory closure gate before production deployment or enabling protected production data.

## 9. Required tests before approval

### Unit tests
- Token generation has at least 256 bits of cryptographic entropy and uses strict base64url encoding.
- Only the secret hash is persisted; raw secrets never enter logs or DTOs.
- JWT validation rejects wrong issuer/audience, expiry, not-before, bad signature, disallowed algorithm and unknown key ID.
- Session expiry and revocation rules are deterministic under an injected clock.
- Staff password validation alone cannot create an AuthSession.
- Provisioning cannot create an Identity user without an explicit valid AccountType; Customer and Staff authentication paths reject the opposite type.
- Access JWT role claims are minted from current server-side role assignments, cannot be overridden by request data, and are rejected when signature/issuer/audience validation fails.
- A role change does not mutate an already-issued JWT; a subsequent refresh uses current role assignments.

### PostgreSQL integration/concurrency tests
- Successful refresh consumes the old token and creates exactly one successor in one transaction.
- Two concurrent refreshes with the same valid token produce at most one successor; the replay policy revokes the session.
- Wrong secret with a known token ID does not revoke the session.
- Reuse of a consumed token revokes its session and active successor.
- Expired/revoked session or token cannot rotate.
- Logout/revocation is idempotent.
- Credential-security events revoke the configured session scope.
- A failed transaction leaves no consumed-without-successor partial state.
- MFA transaction is purpose-bound, expires, has bounded attempts and can be consumed only once.
- A successful MFA transaction creates one staff session; replay cannot create another.

### HTTP/security integration tests
- Access/refresh/MFA secrets are never returned in JSON.
- Cookie flags, paths, expiries and clearing behavior match the configured topology.
- Missing/invalid CSRF token and untrusted/missing Origin are rejected on unsafe browser routes.
- CORS rejects unlisted origins and never reflects arbitrary origins with credentials.
- Refresh errors do not disclose whether a user/session/token exists.
- Logs and Problem Details contain no credentials or sensitive token material.

## 10. Decisions confirmed and remaining before implementation

### Confirmed decisions

1. **Refresh replay:** strict replay revocation is accepted. A valid consumed token presented again revokes the owning session and its active refresh tokens. No grace period is allowed. Frontend single-flight and cross-tab coordination are reliability measures, not a security boundary. A lost successful response can require the user to sign in again.
2. **Account classification:** every Identity user has exactly one explicit server-controlled `AccountType`: `Customer` or `Staff`, independent of roles. Authentication endpoints enforce the matching type. Client input cannot assign or change it.
3. **Authorization:** use server-issued JWT role claims for API authorization, with policy requirements mapped to those roles and no per-request Identity database lookup in the initial model. Role-assignment changes take effect for an existing JWT only when it expires; the 5-minute access-token lifetime plus configured clock skew bounds the normal stale-authorization window. This is an intentional trade-off, not immediate revocation.
4. **Access-token lifetime:** the initial target and maximum are 5 minutes. Configuration may lower but must not raise this limit without a separately reviewed security decision.
5. **Session duration:** customers 30-day absolute / 7-day idle; staff 12-hour absolute / 30-minute idle. Keep configurable. Absolute expiry never slides.
6. **Browser topology and cookie baseline:** `shop.com`, `panel.shop.com`, `api.shop.com`; host-only HttpOnly cookies, Secure in production, SameSite=Lax, exact-origin credentialed CORS and independent CSRF/Origin validation.
7. **Staff MFA cookie and CSRF contract:** issue a five-minute, host-only `__Host-staff-mfa` HttpOnly/Secure/SameSite=Lax cookie containing an opaque transaction credential; it grants only completion/cancellation of that pending MFA transaction. Use the NestJS synchronizer-token CSRF contract with JSON request-token bootstrap (`GET /api/v1/security/csrf-token`), keep request tokens in frontend memory, send them in `X-CSRF-TOKEN`, and independently enforce exact Origin validation for every unsafe cookie-authenticated request. Refresh the antiforgery request token after authentication state changes.
8. **Session management UX and device metadata:** customers can view their own sessions with coarse device/browser label, creation time, last successful refresh and authentication method; IP is not shown. Customers can revoke other sessions while preserving the current one, or explicitly sign out all devices. Staff session revocation by another staff member requires a dedicated security-administration permission and audit event. Metadata is informational, not an authentication factor.
9. **JWT signing and key lifecycle:** use RSA/RS256 with a minimum 3072-bit private key, unique `kid`, active/previous public-key overlap during rotation, private-key isolation, and fail-closed production startup.


### Remaining decisions / implementation prerequisites

1. **Application authenticated-encryption deployment:** choose persistent shared key-ring storage and its at-rest protection for production.

Production application encryption-key storage and at-rest protection must be selected, implemented and verified before production deployment or before enabling production features that depend on protected payloads. This deployment choice does **not** block provider-independent authentication implementation or local development: use isolated developer-local/test key material, keep the production configuration fail-closed, and do not ship a production fallback to ephemeral or container-local keys. All other implementation contracts in this document remain subject to review and the normal phase approval process.
