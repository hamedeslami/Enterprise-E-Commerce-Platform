# Session and Token Lifecycle Design

**Status:** Proposed — design only. No authentication endpoints or token persistence have been implemented by this document. This design must be reviewed before implementation.

## 1. Scope and non-goals

This document defines browser session boundaries, access JWT issuance, opaque refresh-token rotation, replay handling, staff MFA pre-authentication state, logout/revocation, and the database invariants needed to make these operations concurrency-safe.

The browser deployment topology is now decided: the customer storefront is `https://shop.com`, the admin panel is `https://panel.shop.com`, and the API is `https://api.shop.com`. The storefront and admin panel call the API cross-origin with credentials, but all three are same-site under the same HTTPS registrable domain. This document records the resulting browser security boundary. It does not select a signing-key hosting product, SMS vendor, or immediate access-token revocation mechanism.

The API is browser-facing and uses separate HttpOnly cookies for access and refresh credentials. Raw token values must never appear in JSON, URLs, browser storage, application logs, traces, or exception messages.

## 2. Core decisions proposed

1. **One session per authenticated browser/device.** A successful authentication creates an `AuthSession`; another device or browser gets a different session.
2. **One refresh-token family per session.** A session has a linear chain of refresh-token records. At most one token in that chain may be active at a time.
3. **Short-lived access JWT.** Initial target: 5 minutes, configurable up to 10 minutes. It is not refreshed in place; refresh issues a new access JWT.
4. **Opaque refresh credential.** Generate a 256-bit cryptographically random secret. The browser receives an opaque value containing a public token identifier and the secret; the database stores only SHA-256(secret), never the secret.
5. **Single-use rotation.** Every successful refresh consumes the presented token and creates exactly one successor in the same database transaction.
6. **Strict replay response.** A valid, already-consumed refresh token is evidence of replay: revoke that session and its active refresh token. A token-ID match with a secret-hash mismatch is just an invalid credential and must not revoke the session.
7. **Serialize refresh on the client.** The frontend must use a single-flight refresh operation so concurrent requests/tabs do not intentionally submit the same token simultaneously. Use cross-tab coordination where supported (for example, Web Locks or BroadcastChannel), but treat it as reliability coordination, not a security boundary. Server-side conditional updates remain authoritative.
8. **Strict replay is accepted despite its availability trade-off.** Any second presentation of a valid, already-consumed refresh token revokes that session, including a legitimate retry after a lost response or a race that escaped client coordination. No grace period or idempotent replay window is provided. The affected user may need to authenticate again.
9. **JWT-claim authorization is the selected initial strategy.** The API authorizes requests using server-issued role/permission claims in the short-lived access JWT; it does not query the Identity database on every authorized request. Authorization changes therefore become effective when the current access JWT expires (target maximum 5 minutes), unless the session is separately revoked and an immediate-revocation mechanism is later introduced. This bounded staleness is an explicit accepted trade-off.
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

Authorization is based on claims minted by the API after successful authentication. The client cannot submit or override roles, permissions, account type, or authentication assurance. The initial implementation will place server-authoritative role claims in the access JWT. ASP.NET Core authorization policies map policy requirements to those role claims; do not duplicate an expansive permission catalog into every token. API authorization policies evaluate the signed claims without a per-request Identity database lookup. If later requirements introduce user-specific permission grants that cannot be represented by role claims, that is a separate design change.

This deliberately accepts bounded authorization staleness: a role or permission change does not alter an already-issued JWT. With the approved 5-minute access-token lifetime, the normal maximum stale-claim window is the remaining lifetime of that JWT. Refresh must issue claims from current server-side account/role data, not copy claims from the previous token. Do not lengthen access-token lifetime to improve performance without revisiting this decision.

Revoking a refresh session alone does not invalidate an already-issued stateless access JWT. The same residual window applies after role removal or account disablement unless a future immediate-revocation mechanism (such as a session/version check) is explicitly designed and implemented. This design does not claim immediate revocation. Sensitive operations remain subject to their explicit policy requirements, audit logging and other domain-level safeguards; these do not silently imply a database permission lookup on every request.

### Validation

Validate signature, a fixed allow-list of algorithms, issuer, audience, lifetime, not-before and key ID (`kid`). Reject unsigned tokens, algorithm confusion and unknown keys. Signing keys must be managed outside source control and support rotation with a documented overlap period. Do not reuse the ASP.NET Core Data Protection key ring as the JWT signing-key store without an explicit cryptographic/key-management design.

The API extracts the access JWT from the configured access cookie. Do not also accept it from query strings. Whether non-browser bearer clients are supported is a separate API-client decision and must not weaken browser CSRF policy.

## 5. Refresh rotation transaction

Refresh is a state-changing, cookie-authenticated operation. It requires CSRF validation and trusted Origin checks.

Proposed server algorithm:

1. Parse the versioned token format and validate lengths/encoding before database work.
2. Load the token row by token ID and its owning session.
3. Verify the presented secret against `SecretHash` using constant-time comparison. On mismatch, return a generic unauthenticated response; do not revoke the session.
4. Begin a PostgreSQL transaction.
5. Atomically transition the token from active to consumed using a conditional update requiring: not consumed, not revoked, not expired. Also verify the session is active and both session expiry boundaries have not passed.
6. Only the request that changes exactly one row may create a successor token row, link `ReplacedByTokenId`, update `LastSeenAt` and `IdleExpiresAt`, and commit.
7. Return the new access JWT and refresh credential only through Set-Cookie response headers after the transaction commits.
8. If the conditional transition affects zero rows, distinguish internally (without revealing details to the caller):
   - Matching secret and token already consumed: replay detected; revoke the session and all active tokens in that session atomically.
   - Token/session expired or revoked: reject and clear stale cookies.
   - Secret mismatch: reject without revocation.
9. Never issue a successor if the transaction fails or rolls back.

A concurrent second use of the same valid token is treated as replay and revokes the session. This is intentionally strict. The frontend must coordinate refresh requests (single-flight across tabs where feasible). If the server commits rotation but the response is lost, retrying the old token may force re-authentication; this reliability/security trade-off must be documented and tested rather than hidden with an unsafe grace period.

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

## 8. Key and secret separation

Keep distinct key material and purposes:
- JWT signing private key / verification public keys.
- OTP HMAC secret.
- TOTP secret-encryption protection (ASP.NET Core Data Protection or a reviewed equivalent).
- Data Protection key ring for Identity providers and protected application payloads.
- Refresh tokens and MFA transaction secrets generated with a cryptographic RNG; only their hashes persist.

Secrets and key rings must be external to source control, access-controlled, backed up and rotated with a documented recovery plan. Production Data Protection keys must be persistent and shared by API instances that need to unprotect the same payloads. Key storage/encryption provider is still a deployment decision.

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

1. **Refresh replay:** strict replay revocation is accepted. A valid consumed token presented again revokes the owning session and its active refresh token. No grace period is allowed. Frontend single-flight and cross-tab coordination are required reliability measures, but the server remains authoritative. A lost successful response can require the user to sign in again.
2. **Account classification:** every Identity user has exactly one explicit server-controlled `AccountType`: `Customer` or `Staff`, independent of roles. Authentication endpoints enforce the matching type. Client input cannot assign or change it.
3. **Authorization:** use server-issued JWT role/permission claims for API authorization, with no per-request Identity database lookup in the initial model. Claim changes take effect on token expiry; the 5-minute access-token lifetime bounds the normal stale-authorization window. This is an intentional trade-off, not immediate revocation.
4. **Session duration:** customers 30-day absolute / 7-day idle; staff 12-hour absolute / 30-minute idle. Keep configurable. Access JWT target remains 5 minutes.
5. **Browser topology and cookie baseline:** `shop.com`, `panel.shop.com`, `api.shop.com`; host-only HttpOnly cookies, Secure in production, SameSite=Lax, exact-origin credentialed CORS and CSRF/Origin validation.

### Remaining decisions / implementation prerequisites

1. **JWT signing-key deployment:** select asymmetric signing and the managed secret/KMS/HSM approach, including key rotation ownership and recovery.
2. **Session management UX:** decide which device metadata is shown to users and whether customers may revoke other sessions.
3. **MFA transaction cookie and antiforgery bootstrap contract:** finalize transaction-cookie scope and CSRF token delivery/validation before implementing endpoints.
4. **Data Protection deployment:** choose persistent shared key-ring storage and its at-rest protection for production.

No authentication code should be implemented until the remaining prerequisites are reviewed and the security contracts are complete.
