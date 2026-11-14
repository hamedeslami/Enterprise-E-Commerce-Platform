# Security Architecture and Threat Model

**Status:** Proposed, Phase 0. This document is a design baseline, not a security certification or claim of production readiness.

## Assets and trust boundaries

Assets: credentials, access/refresh tokens, sessions, customer PII/addresses, order history, payment references, store configuration/secrets, uploaded media, stock and price integrity.

Boundaries: browser to public API; storefront to admin API; API to PostgreSQL/optional Redis; API to external providers; payment provider webhook to public endpoint; uploaded files to media pipeline; CI/deployment to secrets and artifacts. Treat client input, callbacks, uploads and forwarded headers as untrusted until verified.

## Authentication

Use ASP.NET Core Identity for password hashing, account lifecycle and supported identity primitives unless a documented limitation justifies another choice.

Issue short-lived signed JWT access tokens and opaque cryptographically random refresh tokens. Store both only in separate HttpOnly cookies. Never expose token values in JSON, local/session storage, URLs or logs.

Access token:
- Initial target lifetime 5–10 minutes, configurable.
- Validate signature, issuer, audience, lifetime and allowed algorithm.
- Minimal claims: subject, session ID, issued/expiry times and token ID; no sensitive PII.
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

Deny by default. Initial roles: SuperAdmin, Admin, StoreManager, Customer, SupportAgent. Roles group permissions; policies enforce specific permissions and resource context.

Admin routes require staff permissions, not merely authentication. Customers access only their own profiles, addresses, carts and orders. Guest access uses scoped high-entropy capabilities with limited lifetime, never account privileges. Never trust client role/user ID/ownership/price/stock/discount/payment state. Test horizontal and vertical escalation and IDOR.

## Abuse and application defenses

- Rate-limit login, registration, password reset, refresh, coupon redemption, checkout, search and sensitive endpoints. Consider identity/IP/session dimensions and trusted-proxy configuration.
- Progressive brute-force protection without trivial account-lockout denial of service.
- Bound request size, content type, JSON depth and search input.
- Parameterized SQL; review raw SQL.
- Encode output in frontend. Avoid rendering untrusted HTML; sanitize with maintained allow-list sanitizer if rich text is required.
- Security headers: HSTS in production, nosniff, frame policy, referrer policy and restrictive CSP where applicable.
- HTTPS and correctly configured forwarded headers.
- Secrets outside source control using environment/secret manager; rotate and restrict access.
- Redact credentials, tokens, payment data and sensitive PII from logs/traces.
- Audit privileged admin actions and apply least privilege.

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
| IDOR/escalation | Policies, ownership, deny by default | Cross-user/role matrix tests |
| Price/stock tampering | Server calculations, constraints, concurrency | Tampering/concurrent purchase tests |
| Duplicate checkout/payment | Idempotency, unique constraints, state machine | Duplicate request/webhook tests |
| Malicious upload | Content validation, limits, generated keys | Oversize/traversal/content tests |
| SQL injection | Parameterization, raw SQL review | Fuzzing/code review |
| Secret/PII leakage | Secret manager, redaction, least privilege | Log assertions/deployment review |

## Security release gate

Before production: threat-model review, dependency/container/secrets scanning, auth/CSRF/authorization/IDOR tests, concurrency tests, webhook verification, upload tests, config review, backup/restore exercise and independent security review. Track findings with severity, owner and remediation evidence. Presence of controls alone is not proof of security.
