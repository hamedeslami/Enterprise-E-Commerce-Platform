# Security Architecture and Threat Model

**Status:** Active design baseline, Phase 0/2. This document is not a security certification or claim of production readiness.

## Assets and trust boundaries

Assets: credentials, access/refresh tokens, sessions, customer PII/addresses, order history, payment references, store configuration/secrets, uploaded media, stock and price integrity.

Boundaries: browser to public API; storefront to admin API; API to PostgreSQL/optional Redis; API to external providers; payment provider webhook to public endpoint; uploaded files to media pipeline; CI/deployment to secrets and artifacts. Treat client input, callbacks, uploads and forwarded headers as untrusted until verified.

## Authentication

Use ASP.NET Core Identity for password hashing, account lifecycle and supported identity primitives unless a documented limitation justifies another choice.

### Customer sign-in

Support username/password and SMS OTP. SMS OTP is sent only to a previously verified mobile number and does not implicitly create an account. Registration and phone verification are explicit flows. OTP request responses must not disclose whether a phone number is registered.

### Staff sign-in and MFA

All staff roles (SuperAdmin, Admin, StoreManager, SupportAgent) require username/password plus TOTP MFA. Do not permit SMS OTP as a staff second factor or staff-only login method. Each staff member has an individual account; shared credentials are prohibited. Do not issue a normal session after password validation alone: use a short-lived MFA transaction and issue session cookies only after TOTP or a single-use recovery code succeeds.

TOTP secrets are encrypted at rest. Enrollment requires confirmation of a generated code before MFA becomes active. Recovery codes are stored only as hashes, are single-use, and their use/reset is audited. Staff MFA recovery must not provide an unauthenticated bypass.

### Access and refresh tokens

Issue short-lived signed JWT access tokens and opaque cryptographically random refresh tokens. Store both only in separate HttpOnly cookies. Never expose token values in JSON, local/session storage, URLs or logs.

Access token:
- Initial target lifetime 5–10 minutes, configurable.
- Validate signature, issuer, audience, lifetime and allowed algorithm.
- Minimal claims: subject, session ID, issued/expiry times and token ID; include authentication assurance/method only where needed for policy.
- Key rotation uses key identifiers and documented overlap.

Refresh token:
- Cryptographically secure random value with sufficient entropy.
- Persist only a cryptographic hash, never raw token.
- Bind to user and session/family; track issued, expiry, consumed, revoked and replacement state.
- Rotate on every successful refresh in a transaction.
- Reuse of a consumed token revokes affected family/session and requires re-authentication.
- Revoke sessions on logout, password/security events and admin action.
- Enforce single-use semantics under concurrent refresh attempts.

**Revocation trade-off:** stateless JWT remains valid until its short expiry after refresh-session revocation. Initial design accepts this bounded window. If immediate access revocation is mandatory, add persisted session-version/revocation checks with explicit cache availability/fail-closed policy and tests. Do not claim refresh revocation instantly invalidates an issued JWT.

## Cookies, CSRF and CORS

- HttpOnly=true; Secure=true in production; explicit Path and expiry.
- SameSite follows actual frontend/API topology; do not assume Lax works for cross-site deployments.
- Prefer host-only cookies; set Domain only when required.
- Cross-site cookies require SameSite=None; Secure and robust CSRF defenses.
- State-changing browser requests require anti-forgery token and trusted Origin validation. Referer may be a carefully defined fallback, not the sole defense.
- CORS uses exact allow-listed origins, never wildcard origins with credentials. Allow only required methods/headers.
- Reject missing/untrusted Origin on browser-only unsafe routes per documented policy. Non-browser integrations require separate authentication, not weakened browser CSRF checks.
- Document exact cookie names, anti-forgery bootstrap endpoint/header and frontend credential configuration before integration.

## Authorization

Deny by default. Initial roles: SuperAdmin, Admin, StoreManager, Customer, SupportAgent. Roles group permissions; policies enforce specific permissions and resource context. Staff-only endpoints require staff permissions and completed MFA. Customers access only their own profiles, addresses, carts and orders. Guest access uses scoped high-entropy capabilities with limited lifetime, never account privileges. Never trust client role/user ID/ownership/price/stock/discount/payment state. Test horizontal and vertical escalation and IDOR.

## OTP and authentication abuse controls

### OTP challenge security

Customer SMS OTP is purpose-bound (for example, login, registration verification or phone verification), tied to one normalized verified phone/account and one challenge. Generate six-digit values with a cryptographically secure RNG. Challenge lifetime defaults to two minutes; resend cooldown defaults to 60 seconds; maximum failed verification attempts defaults to five. New challenges invalidate prior active challenges for the same purpose according to an atomic database rule.

A six-digit OTP has a small search space: do not store a plain SHA hash. Store HMAC-SHA-256 using a dedicated secret held outside the database and include challenge ID and purpose in the MAC input. Compare in constant time. Store only challenge metadata, HMAC and attempt/status state; never persist or log raw codes. Verification, attempt increments and single-use consumption must be atomic in PostgreSQL.

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
- Shared atomic PostgreSQL quotas for per-phone and global SMS budgets so multiple API instances cannot bypass limits. Do not rely solely on per-process in-memory counters.
- CAPTCHA/proof-of-human challenges when risk thresholds are crossed, as an additional control rather than the primary limiter.
- Provider circuit breaker, bounded concurrency, strict spend ceiling and no unbounded retries.
- Progressive throttling instead of permanent account lockout that can be weaponized.
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

Before production: threat-model review, dependency/container/secrets scanning, auth/CSRF/authorization/IDOR tests, concurrency tests, webhook verification, upload tests, config review, backup/restore exercise and independent security review. Track findings with severity, owner and remediation evidence. Presence of controls alone is not proof of security.
