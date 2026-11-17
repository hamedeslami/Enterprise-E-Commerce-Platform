CREATE SCHEMA IF NOT EXISTS "identity";

CREATE TYPE "identity"."AccountType" AS ENUM ('CUSTOMER', 'STAFF');
CREATE TYPE "identity"."StaffMfaStatus" AS ENUM ('NOT_ENROLLED', 'ENROLLMENT_PENDING', 'ACTIVE');
CREATE TYPE "identity"."AuthenticationMethod" AS ENUM ('PASSWORD', 'SMS_OTP', 'PASSWORD_TOTP', 'PASSWORD_RECOVERY_CODE');
CREATE TYPE "identity"."SessionRevocationReason" AS ENUM ('LOGOUT', 'SIGN_OUT_ALL', 'PASSWORD_CHANGED', 'PASSWORD_RESET', 'ACCOUNT_DISABLED', 'MFA_RESET', 'REFRESH_REPLAY', 'ADMINISTRATIVE', 'SECURITY_EVENT', 'EXPIRED');
CREATE TYPE "identity"."StaffMfaTransactionPurpose" AS ENUM ('STAFF_SIGN_IN_MFA');
CREATE TYPE "identity"."StaffMfaEnrollmentPurpose" AS ENUM ('STAFF_MFA_ENROLLMENT');
CREATE TYPE "identity"."OtpPurpose" AS ENUM ('CUSTOMER_LOGIN');
CREATE TYPE "identity"."SmsOutboxStatus" AS ENUM ('PENDING', 'PROCESSING', 'SENT', 'FAILED', 'EXPIRED', 'CANCELLED');
CREATE TYPE "identity"."OtpQuotaWindow" AS ENUM ('HOUR', 'DAY');
CREATE TYPE "identity"."AuditActorType" AS ENUM ('CUSTOMER', 'STAFF', 'SYSTEM');

CREATE TABLE "identity"."users" (
  "id" UUID NOT NULL,
  "account_type" "identity"."AccountType" NOT NULL,
  "user_name" VARCHAR(256) NOT NULL,
  "normalized_user_name" VARCHAR(256) NOT NULL,
  "email" VARCHAR(320),
  "normalized_email" VARCHAR(320),
  "email_confirmed" BOOLEAN NOT NULL DEFAULT false,
  "phone_number" VARCHAR(16),
  "phone_number_confirmed" BOOLEAN NOT NULL DEFAULT false,
  "password_hash" VARCHAR(512),
  "security_stamp" VARCHAR(128) NOT NULL,
  "concurrency_stamp" VARCHAR(128) NOT NULL,
  "is_active" BOOLEAN NOT NULL,
  "must_change_password" BOOLEAN NOT NULL,
  "access_failed_count" INTEGER NOT NULL DEFAULT 0,
  "lockout_end" TIMESTAMPTZ(6),
  "mfa_lockout_end" TIMESTAMPTZ(6),
  "staff_mfa_status" "identity"."StaffMfaStatus",
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMPTZ(6) NOT NULL,
  CONSTRAINT "users_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_users_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_users_normalized_user_name" CHECK ("normalized_user_name" <> '' AND "normalized_user_name" = btrim("normalized_user_name")),
  CONSTRAINT "ck_identity_users_normalized_email" CHECK ("normalized_email" IS NULL OR ("normalized_email" <> '' AND "normalized_email" = btrim("normalized_email"))),
  CONSTRAINT "ck_identity_users_phone_number" CHECK ("phone_number" IS NULL OR "phone_number" ~ '^\+989[0-9]{9}$'),
  CONSTRAINT "ck_identity_users_access_failed_count" CHECK ("access_failed_count" >= 0),
  CONSTRAINT "ck_identity_users_staff_mfa_status" CHECK (("account_type" = 'CUSTOMER' AND "staff_mfa_status" IS NULL) OR ("account_type" = 'STAFF' AND "staff_mfa_status" IS NOT NULL))
);
CREATE UNIQUE INDEX "ux_identity_users_normalized_user_name" ON "identity"."users"("normalized_user_name");
CREATE UNIQUE INDEX "ux_identity_users_id_account_type" ON "identity"."users"("id", "account_type");
CREATE INDEX "ix_identity_users_normalized_email" ON "identity"."users"("normalized_email");
CREATE INDEX "ix_identity_users_phone_number" ON "identity"."users"("phone_number");
CREATE UNIQUE INDEX "ux_identity_users_customer_normalized_email" ON "identity"."users"("normalized_email") WHERE "account_type" = 'CUSTOMER' AND "normalized_email" IS NOT NULL;
CREATE UNIQUE INDEX "ux_identity_users_customer_phone_number" ON "identity"."users"("phone_number") WHERE "account_type" = 'CUSTOMER' AND "phone_number" IS NOT NULL;

CREATE TABLE "identity"."roles" (
  "id" UUID NOT NULL,
  "name" VARCHAR(128) NOT NULL,
  "normalized_name" VARCHAR(128) NOT NULL,
  "description" VARCHAR(512),
  "is_system" BOOLEAN NOT NULL DEFAULT false,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMPTZ(6) NOT NULL,
  CONSTRAINT "roles_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_roles_id_uuidv7" CHECK (uuid_extract_version("id") = 7)
);
CREATE UNIQUE INDEX "ux_identity_roles_name" ON "identity"."roles"("name");
CREATE UNIQUE INDEX "ux_identity_roles_normalized_name" ON "identity"."roles"("normalized_name");

CREATE TABLE "identity"."permissions" (
  "id" UUID NOT NULL,
  "key" VARCHAR(160) NOT NULL,
  "description" VARCHAR(512),
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT "permissions_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_permissions_id_uuidv7" CHECK (uuid_extract_version("id") = 7)
);
CREATE UNIQUE INDEX "ux_identity_permissions_key" ON "identity"."permissions"("key");

CREATE TABLE "identity"."user_roles" (
  "user_id" UUID NOT NULL,
  "role_id" UUID NOT NULL,
  "assigned_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "assigned_by" UUID,
  CONSTRAINT "user_roles_pkey" PRIMARY KEY ("user_id", "role_id"),
  CONSTRAINT "fk_identity_user_roles_user" FOREIGN KEY ("user_id") REFERENCES "identity"."users"("id") ON DELETE CASCADE ON UPDATE CASCADE,
  CONSTRAINT "fk_identity_user_roles_role" FOREIGN KEY ("role_id") REFERENCES "identity"."roles"("id") ON DELETE RESTRICT ON UPDATE CASCADE,
  CONSTRAINT "fk_identity_user_roles_assigned_by" FOREIGN KEY ("assigned_by") REFERENCES "identity"."users"("id") ON DELETE SET NULL ON UPDATE CASCADE
);
CREATE INDEX "ix_identity_user_roles_role_id" ON "identity"."user_roles"("role_id");

CREATE TABLE "identity"."role_permissions" (
  "role_id" UUID NOT NULL,
  "permission_id" UUID NOT NULL,
  "granted_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT "role_permissions_pkey" PRIMARY KEY ("role_id", "permission_id"),
  CONSTRAINT "fk_identity_role_permissions_role" FOREIGN KEY ("role_id") REFERENCES "identity"."roles"("id") ON DELETE CASCADE ON UPDATE CASCADE,
  CONSTRAINT "fk_identity_role_permissions_permission" FOREIGN KEY ("permission_id") REFERENCES "identity"."permissions"("id") ON DELETE RESTRICT ON UPDATE CASCADE
);
CREATE INDEX "ix_identity_role_permissions_permission_id" ON "identity"."role_permissions"("permission_id");

CREATE TABLE "identity"."auth_sessions" (
  "id" UUID NOT NULL,
  "user_id" UUID NOT NULL,
  "actor_kind" "identity"."AccountType" NOT NULL,
  "authentication_method" "identity"."AuthenticationMethod" NOT NULL,
  "authenticated_at" TIMESTAMPTZ(6) NOT NULL,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "last_seen_at" TIMESTAMPTZ(6) NOT NULL,
  "absolute_expires_at" TIMESTAMPTZ(6) NOT NULL,
  "idle_expires_at" TIMESTAMPTZ(6) NOT NULL,
  "revoked_at" TIMESTAMPTZ(6),
  "revocation_reason" "identity"."SessionRevocationReason",
  "user_agent_hash" CHAR(64),
  "device_label" VARCHAR(160),
  CONSTRAINT "auth_sessions_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_auth_sessions_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_auth_sessions_idle_expiry" CHECK ("idle_expires_at" <= "absolute_expires_at"),
  CONSTRAINT "ck_identity_auth_sessions_revocation" CHECK (("revoked_at" IS NULL AND "revocation_reason" IS NULL) OR ("revoked_at" IS NOT NULL)),
  CONSTRAINT "fk_identity_auth_sessions_user_actor" FOREIGN KEY ("user_id", "actor_kind") REFERENCES "identity"."users"("id", "account_type") ON DELETE RESTRICT ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_sessions_id_user" ON "identity"."auth_sessions"("id", "user_id");
CREATE INDEX "ix_identity_sessions_user_created" ON "identity"."auth_sessions"("user_id", "created_at");
CREATE INDEX "ix_identity_sessions_active_lookup" ON "identity"."auth_sessions"("user_id", "revoked_at", "absolute_expires_at", "idle_expires_at");

CREATE TABLE "identity"."refresh_tokens" (
  "id" UUID NOT NULL,
  "session_id" UUID NOT NULL,
  "secret_hash" CHAR(64) NOT NULL,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "expires_at" TIMESTAMPTZ(6) NOT NULL,
  "consumed_at" TIMESTAMPTZ(6),
  "revoked_at" TIMESTAMPTZ(6),
  "replaced_by_token_id" UUID,
  CONSTRAINT "refresh_tokens_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_refresh_tokens_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_refresh_tokens_secret_hash" CHECK ("secret_hash" ~ '^[0-9a-f]{64}$'),
  CONSTRAINT "ck_identity_refresh_tokens_consumption" CHECK (("consumed_at" IS NULL AND "replaced_by_token_id" IS NULL) OR ("consumed_at" IS NOT NULL AND "replaced_by_token_id" IS NOT NULL)),
  CONSTRAINT "fk_identity_refresh_tokens_session" FOREIGN KEY ("session_id") REFERENCES "identity"."auth_sessions"("id") ON DELETE CASCADE ON UPDATE CASCADE,
  CONSTRAINT "fk_identity_refresh_tokens_replaced_by" FOREIGN KEY ("replaced_by_token_id") REFERENCES "identity"."refresh_tokens"("id") ON DELETE RESTRICT ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_refresh_tokens_secret_hash" ON "identity"."refresh_tokens"("secret_hash");
CREATE UNIQUE INDEX "ux_identity_refresh_tokens_replaced_by" ON "identity"."refresh_tokens"("replaced_by_token_id");
CREATE UNIQUE INDEX "ux_identity_refresh_tokens_session_id" ON "identity"."refresh_tokens"("session_id", "id");
CREATE INDEX "ix_identity_refresh_tokens_session_created" ON "identity"."refresh_tokens"("session_id", "created_at");
CREATE UNIQUE INDEX "ux_identity_refresh_tokens_one_active_per_session" ON "identity"."refresh_tokens"("session_id") WHERE "consumed_at" IS NULL AND "revoked_at" IS NULL;

CREATE TABLE "identity"."staff_mfa_transactions" (
  "id" UUID NOT NULL,
  "user_id" UUID NOT NULL,
  "secret_hash" CHAR(64) NOT NULL,
  "purpose" "identity"."StaffMfaTransactionPurpose" NOT NULL,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "expires_at" TIMESTAMPTZ(6) NOT NULL,
  "failed_attempts" INTEGER NOT NULL DEFAULT 0,
  "consumed_at" TIMESTAMPTZ(6),
  "cancelled_at" TIMESTAMPTZ(6),
  CONSTRAINT "staff_mfa_transactions_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_staff_mfa_transactions_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_staff_mfa_transactions_attempts" CHECK ("failed_attempts" >= 0),
  CONSTRAINT "fk_identity_staff_mfa_transactions_user" FOREIGN KEY ("user_id") REFERENCES "identity"."users"("id") ON DELETE CASCADE ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_staff_mfa_transactions_secret_hash" ON "identity"."staff_mfa_transactions"("secret_hash");
CREATE INDEX "ix_identity_staff_mfa_transactions_user_created" ON "identity"."staff_mfa_transactions"("user_id", "created_at");
CREATE INDEX "ix_identity_staff_mfa_transactions_expiry" ON "identity"."staff_mfa_transactions"("expires_at");

CREATE TABLE "identity"."staff_authenticators" (
  "id" UUID NOT NULL,
  "user_id" UUID NOT NULL,
  "encrypted_secret" BYTEA NOT NULL,
  "encryption_key_version" VARCHAR(64) NOT NULL,
  "last_accepted_time_step" BIGINT,
  "confirmed_at" TIMESTAMPTZ(6),
  "revoked_at" TIMESTAMPTZ(6),
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMPTZ(6) NOT NULL,
  CONSTRAINT "staff_authenticators_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_staff_authenticators_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "fk_identity_staff_authenticators_user" FOREIGN KEY ("user_id") REFERENCES "identity"."users"("id") ON DELETE CASCADE ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_staff_authenticators_user" ON "identity"."staff_authenticators"("user_id");

CREATE TABLE "identity"."staff_recovery_codes" (
  "id" UUID NOT NULL,
  "user_id" UUID NOT NULL,
  "code_hash" CHAR(64) NOT NULL,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "consumed_at" TIMESTAMPTZ(6),
  "revoked_at" TIMESTAMPTZ(6),
  CONSTRAINT "staff_recovery_codes_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_staff_recovery_codes_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_staff_recovery_codes_hash" CHECK ("code_hash" ~ '^[0-9a-f]{64}$'),
  CONSTRAINT "fk_identity_staff_recovery_codes_user" FOREIGN KEY ("user_id") REFERENCES "identity"."users"("id") ON DELETE CASCADE ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_staff_recovery_codes_user_hash" ON "identity"."staff_recovery_codes"("user_id", "code_hash");
CREATE INDEX "ix_identity_staff_recovery_codes_available" ON "identity"."staff_recovery_codes"("user_id", "consumed_at", "revoked_at");

CREATE TABLE "identity"."staff_mfa_enrollment_invitations" (
  "id" UUID NOT NULL,
  "user_id" UUID NOT NULL,
  "token_hash" CHAR(64) NOT NULL,
  "purpose" "identity"."StaffMfaEnrollmentPurpose" NOT NULL DEFAULT 'STAFF_MFA_ENROLLMENT',
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "expires_at" TIMESTAMPTZ(6) NOT NULL,
  "consumed_at" TIMESTAMPTZ(6),
  "cancelled_at" TIMESTAMPTZ(6),
  "created_by_user_id" UUID NOT NULL,
  CONSTRAINT "staff_mfa_enrollment_invitations_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_staff_mfa_enrollment_invitations_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_staff_mfa_enrollment_invitations_hash" CHECK ("token_hash" ~ '^[0-9a-f]{64}$'),
  CONSTRAINT "fk_identity_staff_mfa_enrollment_invitations_user" FOREIGN KEY ("user_id") REFERENCES "identity"."users"("id") ON DELETE CASCADE ON UPDATE CASCADE,
  CONSTRAINT "fk_identity_staff_mfa_enrollment_invitations_created_by" FOREIGN KEY ("created_by_user_id") REFERENCES "identity"."users"("id") ON DELETE RESTRICT ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_staff_mfa_invitations_token_hash" ON "identity"."staff_mfa_enrollment_invitations"("token_hash");
CREATE INDEX "ix_identity_staff_mfa_invitations_user_created" ON "identity"."staff_mfa_enrollment_invitations"("user_id", "created_at");
CREATE INDEX "ix_identity_staff_mfa_invitations_expiry" ON "identity"."staff_mfa_enrollment_invitations"("expires_at");

CREATE TABLE "identity"."staff_mfa_enrollment_transactions" (
  "id" UUID NOT NULL,
  "user_id" UUID NOT NULL,
  "secret_hash" CHAR(64) NOT NULL,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "expires_at" TIMESTAMPTZ(6) NOT NULL,
  "consumed_at" TIMESTAMPTZ(6),
  "cancelled_at" TIMESTAMPTZ(6),
  CONSTRAINT "staff_mfa_enrollment_transactions_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_staff_mfa_enrollment_transactions_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "fk_identity_staff_mfa_enrollment_transactions_user" FOREIGN KEY ("user_id") REFERENCES "identity"."users"("id") ON DELETE CASCADE ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_staff_enrollment_transactions_secret_hash" ON "identity"."staff_mfa_enrollment_transactions"("secret_hash");
CREATE INDEX "ix_identity_staff_enrollment_transactions_user_created" ON "identity"."staff_mfa_enrollment_transactions"("user_id", "created_at");
CREATE INDEX "ix_identity_staff_enrollment_transactions_expiry" ON "identity"."staff_mfa_enrollment_transactions"("expires_at");

CREATE TABLE "identity"."staff_password_change_transactions" (
  "id" UUID NOT NULL,
  "user_id" UUID NOT NULL,
  "secret_hash" CHAR(64) NOT NULL,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "expires_at" TIMESTAMPTZ(6) NOT NULL,
  "consumed_at" TIMESTAMPTZ(6),
  "cancelled_at" TIMESTAMPTZ(6),
  CONSTRAINT "staff_password_change_transactions_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_staff_password_change_transactions_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "fk_identity_staff_password_change_transactions_user" FOREIGN KEY ("user_id") REFERENCES "identity"."users"("id") ON DELETE CASCADE ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_staff_password_change_transactions_secret_hash" ON "identity"."staff_password_change_transactions"("secret_hash");
CREATE INDEX "ix_identity_staff_password_change_transactions_user_created" ON "identity"."staff_password_change_transactions"("user_id", "created_at");
CREATE INDEX "ix_identity_staff_password_change_transactions_expiry" ON "identity"."staff_password_change_transactions"("expires_at");

CREATE TABLE "identity"."customer_otp_challenges" (
  "id" UUID NOT NULL,
  "user_id" UUID NOT NULL,
  "phone_number" VARCHAR(16) NOT NULL,
  "purpose" "identity"."OtpPurpose" NOT NULL,
  "code_mac" CHAR(64) NOT NULL,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "expires_at" TIMESTAMPTZ(6) NOT NULL,
  "failed_attempts" INTEGER NOT NULL DEFAULT 0,
  "consumed_at" TIMESTAMPTZ(6),
  "invalidated_at" TIMESTAMPTZ(6),
  CONSTRAINT "customer_otp_challenges_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_customer_otp_challenges_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_customer_otp_challenges_phone" CHECK ("phone_number" ~ '^\+989[0-9]{9}$'),
  CONSTRAINT "ck_identity_customer_otp_challenges_mac" CHECK ("code_mac" ~ '^[0-9a-f]{64}$'),
  CONSTRAINT "ck_identity_customer_otp_challenges_attempts" CHECK ("failed_attempts" >= 0),
  CONSTRAINT "fk_identity_customer_otp_challenges_user" FOREIGN KEY ("user_id") REFERENCES "identity"."users"("id") ON DELETE CASCADE ON UPDATE CASCADE
);
CREATE INDEX "ix_identity_otp_challenges_user_purpose_created" ON "identity"."customer_otp_challenges"("user_id", "purpose", "created_at");
CREATE INDEX "ix_identity_otp_challenges_phone_purpose_created" ON "identity"."customer_otp_challenges"("phone_number", "purpose", "created_at");
CREATE INDEX "ix_identity_otp_challenges_expiry" ON "identity"."customer_otp_challenges"("expires_at");

CREATE TABLE "identity"."sms_outbox" (
  "id" UUID NOT NULL,
  "challenge_id" UUID NOT NULL,
  "protected_payload" BYTEA NOT NULL,
  "encryption_key_version" VARCHAR(64) NOT NULL,
  "status" "identity"."SmsOutboxStatus" NOT NULL DEFAULT 'PENDING',
  "attempt_count" INTEGER NOT NULL DEFAULT 0,
  "next_attempt_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "lease_owner" VARCHAR(128),
  "lease_expires_at" TIMESTAMPTZ(6),
  "expires_at" TIMESTAMPTZ(6) NOT NULL,
  "terminal_at" TIMESTAMPTZ(6),
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMPTZ(6) NOT NULL,
  CONSTRAINT "sms_outbox_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_sms_outbox_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_sms_outbox_attempt_count" CHECK ("attempt_count" >= 0 AND "attempt_count" <= 3),
  CONSTRAINT "ck_identity_sms_outbox_lease" CHECK (("lease_owner" IS NULL AND "lease_expires_at" IS NULL) OR ("lease_owner" IS NOT NULL AND "lease_expires_at" IS NOT NULL)),
  CONSTRAINT "fk_identity_sms_outbox_challenge" FOREIGN KEY ("challenge_id") REFERENCES "identity"."customer_otp_challenges"("id") ON DELETE CASCADE ON UPDATE CASCADE
);
CREATE UNIQUE INDEX "ux_identity_sms_outbox_challenge" ON "identity"."sms_outbox"("challenge_id");
CREATE INDEX "ix_identity_sms_outbox_claim" ON "identity"."sms_outbox"("status", "next_attempt_at", "created_at");
CREATE INDEX "ix_identity_sms_outbox_expiry" ON "identity"."sms_outbox"("expires_at");

CREATE TABLE "identity"."otp_send_quotas" (
  "id" UUID NOT NULL,
  "scope_key" VARCHAR(128) NOT NULL,
  "window" "identity"."OtpQuotaWindow" NOT NULL,
  "window_start" TIMESTAMPTZ(6) NOT NULL,
  "send_count" INTEGER NOT NULL,
  "created_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  "updated_at" TIMESTAMPTZ(6) NOT NULL,
  CONSTRAINT "otp_send_quotas_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_otp_send_quotas_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "ck_identity_otp_send_quotas_send_count" CHECK ("send_count" >= 0)
);
CREATE UNIQUE INDEX "ux_identity_otp_send_quotas_scope_window" ON "identity"."otp_send_quotas"("scope_key", "window", "window_start");
CREATE INDEX "ix_identity_otp_send_quotas_window_start" ON "identity"."otp_send_quotas"("window_start");

CREATE TABLE "identity"."audit_events" (
  "id" UUID NOT NULL,
  "actor_type" "identity"."AuditActorType" NOT NULL,
  "actor_user_id" UUID,
  "action" VARCHAR(160) NOT NULL,
  "target_type" VARCHAR(128) NOT NULL,
  "target_id" VARCHAR(128),
  "reason" VARCHAR(512),
  "correlation_id" VARCHAR(128),
  "metadata" JSONB,
  "occurred_at" TIMESTAMPTZ(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT "audit_events_pkey" PRIMARY KEY ("id"),
  CONSTRAINT "ck_identity_audit_events_id_uuidv7" CHECK (uuid_extract_version("id") = 7),
  CONSTRAINT "fk_identity_audit_events_actor" FOREIGN KEY ("actor_user_id") REFERENCES "identity"."users"("id") ON DELETE SET NULL ON UPDATE CASCADE
);
CREATE INDEX "ix_identity_audit_actor_occurred" ON "identity"."audit_events"("actor_user_id", "occurred_at");
CREATE INDEX "ix_identity_audit_target_occurred" ON "identity"."audit_events"("target_type", "target_id", "occurred_at");

CREATE FUNCTION "identity"."enforce_refresh_token_session_bounds"() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  session_row "identity"."auth_sessions"%ROWTYPE;
BEGIN
  SELECT * INTO session_row
  FROM "identity"."auth_sessions"
  WHERE "id" = NEW."session_id";

  IF NOT FOUND THEN
    RAISE EXCEPTION 'Refresh token session does not exist' USING ERRCODE = '23503';
  END IF;

  IF NEW."expires_at" > session_row."absolute_expires_at"
     OR NEW."expires_at" > session_row."idle_expires_at" THEN
    RAISE EXCEPTION 'Refresh token expiry exceeds session expiry bounds' USING ERRCODE = '23514';
  END IF;

  IF NEW."replaced_by_token_id" IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM "identity"."refresh_tokens" successor
    WHERE successor."id" = NEW."replaced_by_token_id"
      AND successor."session_id" = NEW."session_id"
  ) THEN
    RAISE EXCEPTION 'Refresh token successor must belong to the same session' USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$;

CREATE TRIGGER "trg_identity_refresh_token_session_bounds"
BEFORE INSERT OR UPDATE OF "session_id", "expires_at", "replaced_by_token_id"
ON "identity"."refresh_tokens"
FOR EACH ROW EXECUTE FUNCTION "identity"."enforce_refresh_token_session_bounds"();

CREATE FUNCTION "identity"."enforce_actor_account_type"() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  actual_type "identity"."AccountType";
BEGIN
  SELECT "account_type" INTO actual_type
  FROM "identity"."users"
  WHERE "id" = NEW."user_id";

  IF TG_TABLE_NAME = 'customer_otp_challenges' AND actual_type <> 'CUSTOMER' THEN
    RAISE EXCEPTION 'OTP challenges are restricted to Customer accounts' USING ERRCODE = '23514';
  END IF;

  IF TG_TABLE_NAME IN (
    'staff_mfa_transactions',
    'staff_authenticators',
    'staff_recovery_codes',
    'staff_mfa_enrollment_invitations',
    'staff_mfa_enrollment_transactions',
    'staff_password_change_transactions'
  ) AND actual_type <> 'STAFF' THEN
    RAISE EXCEPTION 'MFA persistence is restricted to Staff accounts' USING ERRCODE = '23514';
  END IF;

  RETURN NEW;
END;
$$;

CREATE TRIGGER "trg_identity_staff_mfa_transactions_account_type"
BEFORE INSERT OR UPDATE OF "user_id" ON "identity"."staff_mfa_transactions"
FOR EACH ROW EXECUTE FUNCTION "identity"."enforce_actor_account_type"();
CREATE TRIGGER "trg_identity_staff_authenticators_account_type"
BEFORE INSERT OR UPDATE OF "user_id" ON "identity"."staff_authenticators"
FOR EACH ROW EXECUTE FUNCTION "identity"."enforce_actor_account_type"();
CREATE TRIGGER "trg_identity_staff_recovery_codes_account_type"
BEFORE INSERT OR UPDATE OF "user_id" ON "identity"."staff_recovery_codes"
FOR EACH ROW EXECUTE FUNCTION "identity"."enforce_actor_account_type"();
CREATE TRIGGER "trg_identity_staff_mfa_invitations_account_type"
BEFORE INSERT OR UPDATE OF "user_id" ON "identity"."staff_mfa_enrollment_invitations"
FOR EACH ROW EXECUTE FUNCTION "identity"."enforce_actor_account_type"();
CREATE TRIGGER "trg_identity_staff_enrollment_transactions_account_type"
BEFORE INSERT OR UPDATE OF "user_id" ON "identity"."staff_mfa_enrollment_transactions"
FOR EACH ROW EXECUTE FUNCTION "identity"."enforce_actor_account_type"();
CREATE TRIGGER "trg_identity_staff_password_change_transactions_account_type"
BEFORE INSERT OR UPDATE OF "user_id" ON "identity"."staff_password_change_transactions"
FOR EACH ROW EXECUTE FUNCTION "identity"."enforce_actor_account_type"();
CREATE TRIGGER "trg_identity_customer_otp_account_type"
BEFORE INSERT OR UPDATE OF "user_id" ON "identity"."customer_otp_challenges"
FOR EACH ROW EXECUTE FUNCTION "identity"."enforce_actor_account_type"();
