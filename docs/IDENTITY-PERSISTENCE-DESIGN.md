# Identity Persistence — Prisma / PostgreSQL

**Status:** M3 persistence baseline plus M4 refresh-rotation implementation in progress. **Runtime:** NestJS 12 / Prisma 7. **Database:** PostgreSQL 18.  
**Migration policy:** Fresh development/test database only; no EF migration history or data-copy path is retained.

## Scope

This document describes the Identity persistence boundary and the M4 refresh-rotation service. It does not implement login/authentication ceremonies, password hashing, JWT issuance, OTP verification, TOTP, HTTP authentication endpoints, cookies, CSRF, authorization guards or SMS delivery workers.

All persisted identifiers are application-generated UUIDv7 values. The application helper is `generateUuidV7()`; PostgreSQL 18 checks `uuid_extract_version(id) = 7` on every entity primary key. The generator writes the 48-bit Unix-millisecond timestamp, version and RFC variant bits, and fills the remaining random bits using Node's cryptographic random source. UUIDv7 is an identifier format, not an authorization control.

## Identity aggregate

### Users

`identity.users` is the application-owned account root. `account_type` is a PostgreSQL enum with exactly `CUSTOMER` and `STAFF`; it is mandatory and has no default. It is independent of roles.

- `is_active` and `must_change_password` are mandatory, server-controlled values with no database defaults. Provisioning must explicitly set them.
- Customer rows must keep `must_change_password = false`; Staff rows carry an explicit `staff_mfa_status` (`NOT_ENROLLED`, `ENROLLMENT_PENDING`, `ACTIVE`).
- MFA lockout is separate from ordinary password lockout and is restricted to Staff.
- Confirmed email/mobile flags cannot be true when their corresponding identifier is absent.
- Customer login identifiers use partial unique indexes: normalized email and canonical mobile are unique among Customer rows only. Staff contact details may overlap Customer identifiers.
- `normalized_user_name` remains globally unique; Staff username lookup is separate from Customer login identifier lookup.
- Canonical Iranian mobile is `+989xxxxxxxxx`. The database rejects malformed phone values. Application normalization still owns accepted input forms and digit normalization.

The Prisma enum representation intentionally replaces the legacy EF numeric account-type storage. No legacy production data conversion is part of this migration.

### Roles and permissions

`roles`, `permissions`, `user_roles` and `role_permissions` persist role assignment and permission mapping. Role and permission identifiers are UUIDv7. Role/permission keys and normalized role names are unique. Role assignments are explicit; account type is not inferred from role membership.

## Session and credential persistence

### AuthSession

Each browser/device session belongs to one Identity user. A composite foreign key from `(user_id, actor_kind)` to `users(id, account_type)` prevents a Customer session from being persisted as Staff or vice versa.

Customer defaults are 30-day absolute / 7-day idle; Staff defaults are 12-hour absolute / 30-minute idle. These values are policy inputs to the session service, not hard-coded database defaults. The database enforces `idle_expires_at <= absolute_expires_at`. Revocation has a controlled reason code. IP address and raw User-Agent are not persisted as authentication factors. In the M4 refresh contract, `last_seen_at` is updated together with idle-expiry renewal and therefore `idle_expires_at - last_seen_at` represents the session's configured idle window. Do not update `last_seen_at` independently on ordinary access requests unless the idle-window policy is separately persisted; otherwise the refresh service could no longer recover the configured window.

### RefreshToken

Only a SHA-256 hash of the 256-bit secret is persisted. The database enforces:
- unique token secret hash;
- at most one unconsumed, unrevoked token per session through a partial unique index;
- consumed/replaced state consistency;
- UUIDv7 token identifiers;
- token expiry no later than its owning session's absolute or current idle expiry, using a PostgreSQL trigger;
- successor token belongs to the same session.

The M4 service implements the approved User → AuthSession → RefreshToken row-lock order at explicit READ COMMITTED isolation, constant-time SHA-256 secret verification, one PostgreSQL clock value for expiry decisions, conditional predecessor claiming, atomic successor insertion/finalization and strict replay revocation. The partial unique active-token index requires a transaction-local temporary predecessor revocation before successor insertion; the predecessor is finalized as consumed/replaced with RevokedAt cleared before commit. A failure after the temporary claim throws and rolls back the complete transaction. The service returns the successor credential only after commit; it does not issue JWTs or expose an HTTP endpoint.

## Staff MFA persistence

The schema persists:
- short-lived Staff MFA transactions with hashed pre-authentication secrets;
- one encrypted authenticator secret per Staff user, encryption key version and last accepted TOTP time-step;
- hashed, single-use recovery codes;
- hashed, single-use enrollment invitations and enrollment transactions;
- hashed, restricted post-MFA password-change transactions.

Database triggers reject Customer ownership for Staff-only MFA records. Application-owned versioned AEAD, key custody, TOTP verification, replay prevention and ceremony atomicity remain deferred to the later authentication work and technical-debt gates. Encrypted bytes in this schema are not a claim that production key management is complete.

## Customer OTP and SMS outbox persistence

`customer_otp_challenges` stores only a 64-character HMAC-SHA-256 value, purpose, attempt count and lifecycle timestamps. A database trigger requires the challenge to belong to an active Customer whose verified mobile equals the challenge mobile.

`sms_outbox` stores a protected payload as bytes plus encryption key version, bounded attempt count, retry time, lease and expiry metadata. The payload must be authenticated ciphertext; plaintext OTP or SMS content must never be stored. `otp_send_quotas` stores windowed counters; scope keys must be keyed/opaque values rather than raw phone numbers or IP addresses.

The migration does not add a worker, provider adapter, OTP HMAC secret, encryption key, or rate-limit policy implementation.

## Audit events

`audit_events` stores actor/target identifiers, action, reason, correlation ID and bounded JSON metadata. It is designed for application-level append-only use. Database roles/permissions and retention/immutability operations must be configured separately; the table alone is not tamper-proof.

## PostgreSQL migration-specific enforcement

Prisma schema expresses the relational model. The reviewed SQL migration additionally creates:
- the `identity` schema and PostgreSQL enum types;
- UUIDv7 version checks;
- Customer-only partial unique indexes for email/mobile;
- account-type checks for Staff MFA and Customer OTP rows;
- refresh-token partial uniqueness and expiry/successor triggers;
- identifier, state, lease and attempt-count checks.

These SQL constraints/triggers are intentional PostgreSQL-specific enforcement. Do not replace them with application-only pre-checks.

## Verification and deployment

CI must start from an empty PostgreSQL 18 database, run `prisma validate`, apply the committed migration with `prisma migrate deploy`, then run PostgreSQL integration tests that prove both valid persistence and rejection of invalid states. The same migration is also applied to the disposable PostgreSQL instance in Docker Compose CI.

The migration must not be run against a production database. Development/test reset is allowed only because the owner confirmed there is no important data.

## Deferred implementation gates

- **M4 / TD-MIG-004:** refresh rotation service and PostgreSQL regression tests are implemented on an isolated branch; TD-MIG-004 remains open until CI and review evidence pass.
- **Before first auth endpoint / TD-MIG-001:** password hashing algorithm, parameters, versioned format and rehash policy.
- **Before production TOTP/outbox / TD-MIG-003 and TD-SEC-001:** AEAD construction, key custody, rotation and recovery.
- **Before monetary/BIGINT APIs / TD-MIG-005:** exact TypeScript and JSON representation.
- **Before production JWT / TD-SEC-002:** signing key custody and rotation operations.
- **Before production OTP / TD-SEC-003 and TD-SEC-004:** HMAC secret and SMS provider contract.
- **Before browser auth / TD-MIG-002 and TD-SEC-005:** synchronizer CSRF token, exact Origin, CORS and cookie deployment verification.

No authentication endpoint or production-ready security claim follows from completing M3.
