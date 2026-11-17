import { randomBytes } from 'node:crypto';
import { Pool } from 'pg';
import { generateUuidV7 } from '../src/shared/identifiers/uuidv7.js';

describe('Identity PostgreSQL persistence', () => {
  const pool = new Pool({ connectionString: process.env.DATABASE_URL });

  async function createUser(options: {
    accountType?: 'CUSTOMER' | 'STAFF';
    email?: string | null;
    phoneNumber?: string | null;
  } = {}) {
    const accountType = options.accountType ?? 'CUSTOMER';
    const id = generateUuidV7();
    const username = `user-${id}`;
    const normalizedEmail = options.email ?? null;
    const phoneNumber = options.phoneNumber ?? null;

    await pool.query(
      `INSERT INTO identity.users (
        id, account_type, user_name, normalized_user_name,
        email, normalized_email, phone_number, password_hash,
        security_stamp, concurrency_stamp, is_active, must_change_password,
        staff_mfa_status, updated_at
      ) VALUES ($1, $2::identity."AccountType", $3, $4, $5, $5, $6, $7, $8, $9, true, $10, $11::identity."StaffMfaStatus", NOW())`,
      [
        id,
        accountType,
        username,
        username.toUpperCase(),
        normalizedEmail,
        phoneNumber,
        'test-password-hash',
        randomBytes(16).toString('hex'),
        randomBytes(16).toString('hex'),
        accountType === 'STAFF',
        accountType === 'STAFF' ? 'ENROLLMENT_PENDING' : null
      ]
    );

    return id;
  }

  async function expectPgCode(operation: Promise<unknown>, code: string) {
    try {
      await operation;
      throw new Error(`Expected PostgreSQL error ${code}`);
    } catch (error) {
      expect((error as { code?: string }).code).toBe(code);
    }
  }

  afterAll(async () => {
    await pool.end();
  });

  it('applies the Identity schema and creates all persistence tables', async () => {
    const result = await pool.query<{ table_name: string }>(
      `SELECT table_name
       FROM information_schema.tables
       WHERE table_schema = 'identity' AND table_type = 'BASE TABLE'`
    );

    const names = result.rows.map((row) => row.table_name);
    expect(names).toEqual(
      expect.arrayContaining([
        'users',
        'roles',
        'permissions',
        'user_roles',
        'role_permissions',
        'auth_sessions',
        'refresh_tokens',
        'staff_mfa_transactions',
        'staff_authenticators',
        'staff_recovery_codes',
        'staff_mfa_enrollment_invitations',
        'staff_mfa_enrollment_transactions',
        'staff_password_change_transactions',
        'customer_otp_challenges',
        'sms_outbox',
        'otp_send_quotas',
        'audit_events'
      ])
    );
  });

  it('rejects non-UUIDv7 primary identifiers at the database boundary', async () => {
    await expectPgCode(
      pool.query(
        `INSERT INTO identity.roles (id, name, normalized_name, updated_at)
         VALUES ($1, 'Invalid', 'INVALID', NOW())`,
        ['00000000-0000-4000-8000-000000000001']
      ),
      '23514'
    );
  });

  it('enforces normalized email uniqueness only for Customer accounts', async () => {
    await createUser({ email: 'shared@example.test' });

    await expectPgCode(
      createUser({ email: 'shared@example.test' }),
      '23505'
    );

    await expect(createUser({ accountType: 'STAFF', email: 'shared@example.test' })).resolves.toBeTruthy();
  });

  it('enforces the persisted account type on session ownership', async () => {
    const customerId = await createUser();

    await expectPgCode(
      pool.query(
        `INSERT INTO identity.auth_sessions (
          id, user_id, actor_kind, authentication_method, authenticated_at,
          last_seen_at, absolute_expires_at, idle_expires_at
        ) VALUES ($1, $2, 'STAFF', 'PASSWORD_TOTP', NOW(), NOW(),
          NOW() + INTERVAL '12 hours', NOW() + INTERVAL '30 minutes')`,
        [generateUuidV7(), customerId]
      ),
      '23503'
    );
  });

  it('allows at most one active refresh token per session', async () => {
    const userId = await createUser();
    const sessionId = generateUuidV7();

    await pool.query(
      `INSERT INTO identity.auth_sessions (
        id, user_id, actor_kind, authentication_method, authenticated_at,
        last_seen_at, absolute_expires_at, idle_expires_at
      ) VALUES ($1, $2, 'CUSTOMER', 'PASSWORD', NOW(), NOW(),
        NOW() + INTERVAL '30 days', NOW() + INTERVAL '7 days')`,
      [sessionId, userId]
    );

    const insertToken = (id: string, hash: string) =>
      pool.query(
        `INSERT INTO identity.refresh_tokens (id, session_id, secret_hash, expires_at)
         VALUES ($1, $2, $3, NOW() + INTERVAL '1 day')`,
        [id, sessionId, hash]
      );

    await insertToken(generateUuidV7(), randomBytes(32).toString('hex'));
    await expectPgCode(
      insertToken(generateUuidV7(), randomBytes(32).toString('hex')),
      '23505'
    );
  });

  it('rejects refresh-token expiry beyond the owning session bounds', async () => {
    const userId = await createUser();
    const sessionId = generateUuidV7();

    await pool.query(
      `INSERT INTO identity.auth_sessions (
        id, user_id, actor_kind, authentication_method, authenticated_at,
        last_seen_at, absolute_expires_at, idle_expires_at
      ) VALUES ($1, $2, 'CUSTOMER', 'PASSWORD', NOW(), NOW(),
        NOW() + INTERVAL '30 days', NOW() + INTERVAL '7 days')`,
      [sessionId, userId]
    );

    await expectPgCode(
      pool.query(
        `INSERT INTO identity.refresh_tokens (id, session_id, secret_hash, expires_at)
         VALUES ($1, $2, $3, NOW() + INTERVAL '8 days')`,
        [generateUuidV7(), sessionId, randomBytes(32).toString('hex')]
      ),
      '23514'
    );
  });

  it('prevents Customer accounts from receiving Staff MFA persistence', async () => {
    const customerId = await createUser();

    await expectPgCode(
      pool.query(
        `INSERT INTO identity.staff_mfa_transactions (
          id, user_id, secret_hash, purpose, expires_at
        ) VALUES ($1, $2, $3, 'STAFF_SIGN_IN_MFA', NOW() + INTERVAL '5 minutes')`,
        [generateUuidV7(), customerId, randomBytes(32).toString('hex')]
      ),
      '23514'
    );
  });
});
