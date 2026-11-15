# Security Architecture and Threat Model

**Status:** Active design baseline, Phase 0/2. This document is not a security certification or claim of production readiness.

## Assets and trust boundaries

Assets: credentials, access/refresh tokens, sessions, customer PII/addresses, order history, payment references, store configuration/secrets, uploaded media, stock and price integrity.

Boundaries: browser to public API; storefront to admin API; API to PostgreSQL/optional Redis; API to external providers; payment provider webhook to public endpoint; uploaded files to media pipeline; CI/deployment to secrets and artifacts. Treat client input, callbacks, uploads and forwarded headers as untrusted until verified.

## Authentication

Use ASP.NET Core Identity for password hashing, account lifecycle and supported identity primitives unless a documented limitation justifies another choice.

### Customer sign-in

Customer password sign-in accepts one `identifier` plus `password`; the identifier is either email or mobile number. The server normalizes and classifies it; the client cannot choose an account type or query mode.

- Email + password is accepted only when `EmailConfirmed == true`.
- Mobile + password is accepted only when `PhoneNumberConfirmed == true`.
- SMS OTP is available only for an existing Customer account with a previously verified mobile number.
- OTP login never creates an account. Registration and verification remain explicit flows.
- Customer authentication endpoints must reject Staff accounts, even if a contact identifier matches.
- Public responses must not reveal whether an identifier exists, is unverified, belongs to Staff, is locked, or has an incorrect password.

Customer email is normalized using ASP.NET Core Identity normalization; do not apply provider-specific dot/plus-tag rewriting. Iranian mobile inputs are normalized to canonical E.164 (`+989xxxxxxxxx`); accept only documented input forms and reject ambiguous values. PostgreSQL must enforce uniqueness for normalized Customer email and canonical Customer mobile. Application pre-checks are not authoritative. Staff usernames remain unique through Identity's normalized-username constraint. Customer username is not a login identifier; if Identity requires one, use a server-generated internal value that Customer login endpoints never accept.

### Staff sign-in and MFA

All staff roles (SuperAdmin, Admin, StoreManager, SupportAgent) require username/password plus TOTP MFA. A single-use recovery code is the controlled recovery alternative. Do not permit SMS OTP as a staff second factor or staff-only login method. Each staff member has an individual account; shared credentials are prohibited. Staff cannot self-register: only SuperAdmin with StaffProvisioning permission may create accounts and set/reset passwords. Initial/reset passwords are temporary and communicated through a trusted channel, never embedded in the MFA invitation or SMS queue. A non-null persisted server-controlled `MustChangePassword` flag is set on Staff creation and every admin password reset. Its migration backfills existing Staff rows to true and Customer rows to false. After completing MFA, the employee must change the temporary password before receiving a normal session or accessing the application. A separate 10-minute post-MFA credential authorizes only password change; no AuthSession/access/refresh credential exists before completion. Password change accepts only the new password, checks it differs from the current temporary password, applies Identity policy, and clears the flag only in the same PostgreSQL transaction that consumes the restricted credential and creates the normal session and initial refresh token. All provisioning/password/MFA reset actions require completed SuperAdmin MFA and audit.

Password success creates only a short-lived, purpose-bound MFA transaction and the dedicated `__Host-staff-mfa` cookie (maximum five minutes). It MUST NOT create a normal session, access JWT or refresh token. After TOTP or a single-use recovery code succeeds, issue normal session credentials only if `MustChangePassword=false`. Otherwise issue only a separate `__Host-staff-password-change` credential (maximum 10 minutes) restricted to the password-change endpoint; no normal session/token exists until password change succeeds.

TOTP secrets are encrypted at rest. SuperAdmin initiates a single-use, 24-hour maximum MFA enrollment invitation; the employee redeems it with their username and admin-provided password, then receives the TOTP provisioning secret once. SuperAdmin never sees the TOTP secret or recovery codes. Enrollment is not complete until the employee confirms the first TOTP code; enrollment completion creates no session. MFA reset invalidates old authenticators/recovery codes and all sessions, and Staff login remains blocked until reenrollment. Recovery codes are stored only as hashes, are single-use, and their use/reset is audited. Staff MFA recovery must not provide an unauthenticated bypass.

### Access and refresh tokens

Issue short-lived signed JWT access tokens and opaque cryptographically random refresh tokens. Store both only in separate HttpOnly cookies. Never expose token values in JSON, local/session storage, URLs or logs.

Access token:
- Initial target and maximum lifetime: 5 minutes. Configuration may lower it but must not silently lengthen it.
- Validate signature, issuer, audience, lifetime and fixed allowed algorithm.
- Minimal claims: subject, session ID, issued/expiry times and token ID; include server-derived roles and authentication assurance only where required by policy.
- Key rotation uses key identifiers and documented overlap.

Refresh token:
- Cryptographically secure random value with 256-bit entropy.
- Persist only a cryptographic hash, never raw token.
- Bind to user and session/family; track issued, expiry, consumed, revoked and replacement state.
- Rotate on every successful refresh in a transaction.
- Reuse of a consumed token revokes affected session/family and requires re-authentication.
- Revoke sessions on logout, password/security events and admin action.
- Enforce single-use semantics under concurrent refresh attempts.

**Revocation trade-off:** stateless JWT remains valid until its short expiry after refresh-session revocation. Initial design accepts this bounded window. If immediate access revocation is mandatory, add persisted session-version/revocation checks with explicit cache availability/fail-closed policy and tests. Do not claim refresh revocation instantly invalidates an issued JWT.

## Cookies, CSRF and CORS

- HttpOnly=true; Secure=true in production; explicit Path and expiry.
- Production topology is `https://shop.com`, `https://panel.shop.com`, and `https://api.shop.com`; both frontends call the API cross-origin with credentials, while all three hosts are same-site under HTTPS.
- Issue authentication cookies from `api.shop.com` as host-only cookies: omit `Domain`; use `Secure`, `HttpOnly`, `Path=/`, and explicit `SameSite=Lax`. Prefer `__Host-` names where their requirements are met. The Staff enrollment transaction uses a separate `__Host-staff-enrollment` cookie (maximum 10 minutes) that grants enrollment only, never an authenticated session.
- SameSite and CORS do not replace CSRF protection: sibling subdomains are same-site. Every unsafe cookie-authenticated request requires both ASP.NET Core antiforgery validation and exact Origin validation.
- CORS allows exactly `https://shop.com` and `https://panel.shop.com` with credentials; never use wildcard or arbitrary Origin reflection.
- Missing Origin, untrusted Origin, missing/invalid antiforgery token are rejected. Do not use Referer as a fallback.
- Browser clients send credentials and keep the antiforgery request token in memory; send it in `X-CSRF-TOKEN`. Bootstrap endpoint: `GET /api/v1/security/csrf-token`, with `Cache-Control: no-store`.

## Session lifecycle and browser session management

The detailed session/token contract is maintained in [SESSION-TOKEN-DESIGN.md](SESSION-TOKEN-DESIGN.md). Initial approved policy: Customer sessions have a 30-day absolute and 7-day idle lifetime; Staff sessions have a 12-hour absolute and 30-minute idle lifetime. Absolute expiry never slides; idle expiry extends only after successful refresh. Refresh replay is strict: presenting a valid consumed token again revokes its owning session and active successor; no grace period is allowed. A token-ID match with a wrong secret must not revoke a session.

Customers and Staff may list only their own sessions, see the current session, revoke other sessions while preserving the current one, or call DELETE /api/v1/auth/sessions to idempotently revoke all of their sessions including current. The all-session endpoint requires CSRF and exact Origin validation and clears current-browser auth cookies. Do not display IP addresses or treat IP/User-Agent as authentication factors. Staff revocation of another staff member's session requires a dedicated security-administration permission and an audit event.

Staff password success creates only a short-lived MFA transaction, never a normal session. The pre-authentication credential uses the dedicated `__Host-staff-mfa` cookie (maximum five minutes) and grants only completion/cancellation of that transaction. A separate `__Host-staff-enrollment` cookie grants only a maximum 10-minute enrollment ceremony and never a session. A normal staff session is created only after TOTP or a single-use recovery code succeeds. Five failed MFA submissions in a rolling 15-minute account window cause a 15-minute temporary authentication lockout across devices/transactions; atomically invalidate pending MFA transactions and revoke active Staff sessions/refresh credentials. This does not change the admin-controlled IsActive flag. Already-issued stateless JWTs remain valid only for the bounded residual period accepted by AR-SEC-001.

## Account activation and authorization

`ApplicationUser.IsActive` is a non-null, server-controlled account lifecycle flag. It is independent of Identity's temporary `LockoutEnd` and the Staff `MustChangePassword` ceremony flag. Existing Customer and Staff rows are backfilled active; new-account provisioning must explicitly set the state. Public DTOs cannot set it. An inactive account cannot authenticate, complete pending OTP/MFA transactions, create a session or rotate a refresh token. Privileged deactivation atomically invalidates pending authentication transactions and revokes all active sessions and refresh credentials; reactivation does not restore them. Already-issued stateless access JWTs may remain valid only for the bounded residual lifetime accepted by AR-SEC-001 (maximum five minutes plus configured clock skew). All account-sensitive authentication operations use the same PostgreSQL lock order: owning user row, pending OTP/MFA row, affected session rows in stable UUID order, then refresh-token rows. Refresh rotation must follow user → session → token (extending the current session → token order); every path revalidates `IsActive` while holding the user-row lock. No operation may acquire a child credential lock and then its owning user lock.

Deny by default. Initial roles: SuperAdmin, Admin, StoreManager, Customer, SupportAgent. Roles are coarse groupings; ASP.NET Core authorization policies map endpoint access requirements to server-issued role claims in the access JWT and enforce resource ownership/context. Every access JWT also carries exactly one signed `account_type` claim with lowercase value `customer` or `staff`, derived from persisted `AccountType`. This claim is separate from role claims. Policies for protected endpoints require the expected account-type claim in addition to the required role/policy; missing, malformed or mismatched values are denied. The initial model does not perform an Identity database lookup on every authorized request. Staff-only endpoints require the appropriate staff role/policy and completed MFA. Customers access only their own profiles, addresses, carts and orders. Guest access uses scoped high-entropy capabilities with limited lifetime, never account privileges. Never trust client role, permission, account type, user ID, ownership, price, stock, discount, payment state or authentication assurance. Test horizontal and vertical escalation and IDOR.

## OTP and authentication abuse controls

### OTP challenge security

Customer SMS OTP is purpose-bound to Customer login for this slice, tied to one normalized verified phone/account and one challenge. Generate six-digit values with a cryptographically secure RNG. Challenge lifetime defaults to two minutes; resend cooldown defaults to 60 seconds; maximum failed verification attempts defaults to five. New challenges invalidate prior active challenges for the same purpose according to an atomic database rule.

A six-digit OTP has a small search space: do not store a plain SHA hash. Store HMAC-SHA-256 using a dedicated secret held outside the database and include challenge ID and purpose in the MAC input. Compare in constant time. Store only challenge metadata, HMAC and attempt/status state in the challenge record; never persist OTP in plaintext or log it. To support asynchronous delivery, a PostgreSQL transactional outbox may temporarily hold the destination/message only as authenticated Data Protection ciphertext; clear that payload immediately on terminal delivery or expiry, and purge terminal metadata after 24 hours. The worker must never send after the challenge's two-minute expiry or after resend invalidation. Verification, attempt increments, quota consumption and outbox insertion must be atomic in PostgreSQL.

### SMS transactional outbox and provider failover

Use a PostgreSQL transactional outbox for SMS OTP delivery. Challenge creation, quota consumption and outbox insertion commit together. The Identity-owned worker claims work with database leases/row locking, checks database time immediately before sending, and never sends an expired, consumed or resend-invalidated challenge. The outbox TTL is the same as the OTP challenge TTL (initially two minutes).

The challenge table stores only the HMAC. The outbox may temporarily store the destination and message only as authenticated Data Protection ciphertext, never plaintext. The worker decrypts only in memory; encrypted payload is erased immediately after delivery reaches a terminal state or expiry, and terminal metadata is purged after 24 hours. Production key-ring controls are covered by TD-SEC-001.

Configure primary and secondary provider adapters in priority order. Fail over on provider/network unavailability, timeout, 429, 5xx, circuit-open and provider configuration/authentication failure; alert on configuration failures. Permanent message-level rejection is terminal. Use provider-specific circuit breakers, at most three total attempts per message and bounded backoff (initially 2s then 5s). Reuse the same challenge/code for retries; pass the stable challenge ID as idempotency key where supported. An ambiguous timeout may produce duplicate delivery, but duplicates carry the same OTP and do not extend expiry. OTP verification may succeed while delivery status is pending/unknown; it must not depend on a provider delivery receipt. Do not extend challenge TTL to wait for a provider. A definite permanent rejection invalidates the challenge; exhausted transient/ambiguous attempts leave it valid only until its original expiry. Expired queue work is purged and never sent.

The API returns a generic accepted response after the outbox transaction commits, not a claim of successful delivery. Provider failover does not bypass phone/IP/global quotas, concurrency limits or spend budgets. Redis or a broker is not required for the initial implementation; reconsider only with measured need.

### Initial configurable quotas

- Per normalized phone: maximum five sends per rolling hour and ten per rolling 24 hours.
- Per phone: minimum 60 seconds between sends.
- Per challenge: maximum five failed verification attempts.
- Per IP/network: endpoint-specific request limits, configured with correct trusted-proxy handling.
- Global: provider concurrency, throughput and daily message/spend budget.

These are starting values to tune against actual traffic and provider constraints. Apply rate limiting before provider calls and account lookup where possible. Return generic responses to prevent account enumeration.

### Layered abuse and DDoS controls

- Network edge/WAF/reverse proxy limits, connection/request-size controls and upstream DDoS mitigation.
- ASP.NET Core rate limiting for coarse per-IP/network limits. It is not a substitute for edge DDoS protection.
- Shared atomic PostgreSQL quotas for per-phone and global SMS budgets so multiple API instances cannot bypass them. Do not rely solely on per-process in-memory counters.
- CAPTCHA/proof-of-human challenges when risk thresholds are crossed, as an additional control rather than the primary limiter.
- PostgreSQL transactional outbox with expiry-aware worker; ordered primary and secondary SMS providers; failover on provider/network/timeout/429/5xx/circuit-open failures; provider-specific circuit breakers; at most three attempts per message with bounded backoff; stable challenge/idempotency key where supported; strict spend ceiling and no unbounded retries.
- Progressive throttling for public/customer login paths to reduce account-lockout denial of service. Staff MFA has the separately approved rule: the fifth failed factor submission within 15 minutes triggers a 15-minute temporary auth lock, applied only after the correct Staff password has been proven and counted atomically across transactions/devices.
- Alerts for IP/phone fan-out, failure spikes, unusual send rates and provider spend.
- Redis may be added later if measured traffic requires distributed rate limiting; define behavior during Redis outage before adoption.

## Abuse and application defenses

- Rate-limit login, registration, password reset, refresh, coupon redemption, checkout, search and sensitive endpoints. Consider identity/IP/session dimensions and trusted-proxy configuration.
- Progressive brute-force protection without trivial account-lockout denial of service.
- Bound request size, content type, JSON depth and search input.
- Parameterized SQL; review raw SQL.
- Encode output in frontend. Avoid rendering untrusted HTML; sanitize with maintained allow-list sanitizer if rich text is required.
- Security headers: HSTS in production, nosniff, frame policy, referrer policy and restrictive CSP where applicable.
- HTTPS and correctly configured forwarded headers.
- Secrets outside source control using environment/secret manager; rotate and restrict access.
- Redact credentials, tokens, OTPs, phone numbers where not needed, payment data and sensitive PII from logs/traces.
- Audit privileged admin actions, MFA recovery, staff session revocation and apply least privilege.

## Upload security

Allow-list required extensions and MIME types; inspect signatures/content, not just client headers. Enforce byte/dimension limits; reject traversal and archive bombs. Generate storage keys server-side; never use user filename as path. Store outside executable web roots or in object storage. Serve safe content disposition/type. Scan/process asynchronously where threat model requires. Authorize upload/read/replace/delete independently.

## Payments and webhooks

Never store PAN/CVV. Delegate sensitive entry to compliant hosted/tokenized gateway. Verify webhook signature against exact raw body using provider rules and constant-time comparison; apply timestamp/replay windows; persist provider event IDs uniquely. Process idempotently and reconcile server-to-server. Redirect/client assertion is not proof of payment. Keep provider secrets out of logs/source/API responses.

## Data Protection deployment policy

Production must use a persistent Data Protection key ring shared by all API instances that need to unprotect the same payloads, with one explicit stable ApplicationName and isolated key rings for Development, Staging and Production. Protect key-ring material at rest using an independently managed certificate or KMS-backed mechanism; restrict access to the API workload and authorized operators. Define protected backup, restore, rotation and compromise-recovery procedures and rehearse restoration. Production startup must fail closed if persistent shared storage or at-rest protection is missing or invalid; no ephemeral/container-local fallback is allowed in production. Local development may use a developer-local key ring or isolated Docker volume.

The storage/encryption provider is intentionally not selected until hosting is known. This is tracked as **TD-SEC-001** in [TECHNICAL-DEBT.md](TECHNICAL-DEBT.md), with a mandatory gate before production deployment or enabling protected production data. JWT signing-key custody, OTP HMAC secret provisioning, SMS provider, production origins/TLS, WAF/trusted proxy, distributed rate limiting, backup/DR and privacy retention are also tracked there. The register is authoritative and must be reviewed at the start and end of every phase.

## Data and operations

Minimize PII and define retention, deletion/export and backup retention for target jurisdiction. Encrypt traffic; use managed at-rest encryption for DB/backups/object storage. Least-privilege DB credentials; separate migration/runtime credentials where practical. Scan dependencies and container images. Health endpoints disclose no secrets/topology. Use structured logs, correlation, audit events and alerts for refresh reuse, auth abuse and webhook failures.

## Threat model and verification

| Threat | Main controls | Required tests/review |
|---|---|---|
| Credential stuffing | Rate limits, password policy, monitoring | Auth abuse tests |
| Refresh replay | Hash-only storage, rotation, family detection | Concurrent refresh/replay tests |
| CSRF | Anti-forgery, Origin, SameSite, strict CORS | Missing token/hostile origin tests |
| XSS/token theft | HttpOnly, output encoding, CSP | Security review/integration |
| IDOR/escalation | Policies, ownership, deny by default | Cross-user/role tests |
| OTP brute force | HMAC, expiry, attempt budget, single use | Expiry/replay/concurrent verification |
| SMS pumping/cost abuse | Phone/IP/global quotas, CAPTCHA, spend budget | Concurrent quota/provider tests |
| DDoS | Edge mitigation, app throttles, bounded provider calls | Load/operational review |
| Price/stock tampering | Server calculations, constraints, concurrency | Tampering/concurrent purchase tests |
| Duplicate checkout/payment | Idempotency, unique constraints, state machine | Duplicate request/webhook tests |
| Malicious upload | Content validation, limits, generated keys | Oversize/traversal/content tests |
| SQL injection | Parameterization, raw SQL review | Fuzzing/code review |
| Secret/PII leakage | Secret manager, redaction, least privilege | Log assertions/deployment review |

## Security release gate

Before production: threat-model review, dependency/container/secrets scanning, auth/CSRF/authorization/IDOR tests, concurrency tests (including MFA lockout and all-session revocation), OTP outbox/provider failover/expiry tests, webhook verification, upload tests, config review, backup/restore exercise and independent security review. Track findings with severity, owner and remediation evidence. Presence of controls alone is not proof of security.


## Slice 2C authentication design clarifications

The implementation-ready details for this slice are maintained in [SLICE-2C-AUTHENTICATION-DESIGN.md](SLICE-2C-AUTHENTICATION-DESIGN.md). The following decisions are mandatory for implementation and refine the general policies above:

- TOTP profile: RFC 6238, HMAC-SHA-1, six digits, 30-second step, T0=0 and a fixed ±1 step acceptance window. Persist LastAcceptedTimeStep per authenticator and advance it atomically; reject any step not greater than the last accepted step.
- OTP/MFA/recovery consumption, AuthSession insertion and initial RefreshToken insertion must commit or roll back in one PostgreSQL transaction. JWT signing must complete before commit; a signing failure rolls back the transaction. Add rollback and concurrent duplicate-submission tests.
- `IsActive` is checked at every authentication/session-creation/refresh boundary; deactivation atomically invalidates pending authentication transactions and revokes active sessions/refresh credentials.
- Access JWT includes the signed `account_type` claim (`customer` or `staff`) derived only from persisted `AccountType`; ASP.NET Core policies enforce it independently from role claims.
- Customer uniqueness uses PostgreSQL partial unique indexes on identity.AspNetUsers NormalizedEmail and PhoneNumber where AccountType=1 and the indexed value is not NULL. Preserve the global unique normalized-username index. Empty identifiers are rejected/normalized to NULL; canonical Iranian phone format is enforced by a database check. AccountType conversion and session revocation are one privileged audited transaction.
- Both Customer and Staff can list their own sessions and revoke their own non-current sessions. Staff-to-Staff revocation requires a dedicated SecurityAdmin permission, target ownership validation, completed MFA and audit.
- Recovery codes contain at least 128 bits of CSPRNG entropy, are displayed once in unpadded Base32, and are stored only as domain-separated SHA-256 hashes. Verification responses must not reveal code existence.
- A committed session whose cookies are lost in transit is unreachable and expires no later than its initial idle/absolute expiry; cleanup revokes expired sessions. Strict refresh replay remains unchanged.
- Production cookie names are __Host-access-token, __Host-refresh-token, __Host-staff-mfa, __Host-staff-enrollment and __Host-staff-password-change, with Secure, HttpOnly, SameSite=Lax, Path=/ and no Domain. Development uses separate dev-prefixed names only on local HTTP; production never falls back to insecure cookies. Restricted enrollment/password-change cookies grant no normal session authority and are cleared on terminal lifecycle transitions. Cookie clearing follows the issuance scope and credential lifecycle.
