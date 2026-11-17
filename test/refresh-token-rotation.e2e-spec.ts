import { randomBytes } from 'node:crypto';
import { UnauthorizedException } from '@nestjs/common';
import { Pool } from 'pg';
import { PrismaService } from '../src/shared/database/prisma.service.js';
import { generateUuidV7 } from '../src/shared/identifiers/uuidv7.js';
import {
  formatRefreshToken,
  hashRefreshTokenSecret,
  parseRefreshToken
} from '../src/modules/identity/refresh-tokens/refresh-token.crypto.js';
import { RefreshTokenService } from '../src/modules/identity/refresh-tokens/refresh-token.service.js';

describe('Atomic refresh token rotation (PostgreSQL)', () => {
  const pool = new Pool({ connectionString: process.env.DATABASE_URL });
  const prisma = new PrismaService();
  const service = new RefreshTokenService(prisma);

  async function createFixture(options: {
    sessionState?: 'ACTIVE' | 'IDLE_EXPIRED' | 'ABSOLUTE_EXPIRED' | 'REVOKED';
    tokenExpired?: boolean;
    consumed?: boolean;
    userActive?: boolean;
  } = {}) {
    const userId = generateUuidV7();
    const sessionId = generateUuidV7();
    const tokenId = generateUuidV7();
    const secret = randomBytes(32);
    const state = options.sessionState ?? 'ACTIVE';

    await pool.query(
      `INSERT INTO identity.users (
        id, account_type, user_name, normalized_user_name, password_hash,
        security_stamp, concurrency_stamp, is_active, must_change_password,
        updated_at
      ) VALUES ($1, 'CUSTOMER', $2, $2, $3, $4, $5, $6, false, NOW())`,
      [
        userId,
        `refresh-${userId}`,
        'test-password-hash',
        randomBytes(16).toString('hex'),
        randomBytes(16).toString('hex'),
        options.userActive ?? true
      ]
    );

    await pool.query(
      `INSERT INTO identity.auth_sessions (
        id, user_id, actor_kind, authentication_method, authenticated_at,
        last_seen_at, absolute_expires_at, idle_expires_at,
        revoked_at, revocation_reason
      ) VALUES (
        $1, $2, 'CUSTOMER', 'PASSWORD', NOW(), NOW(),
        CASE WHEN $3 = 'ABSOLUTE_EXPIRED' THEN NOW() - INTERVAL '1 hour'
             ELSE NOW() + INTERVAL '30 days' END,
        CASE WHEN $3 = 'IDLE_EXPIRED' THEN NOW() - INTERVAL '1 minute'
             WHEN $3 = 'ABSOLUTE_EXPIRED' THEN NOW() - INTERVAL '2 hours'
             ELSE NOW() + INTERVAL '7 days' END,
        CASE WHEN $3 = 'REVOKED' THEN NOW() ELSE NULL END,
        CASE WHEN $3 = 'REVOKED' THEN 'LOGOUT'::identity."SessionRevocationReason" ELSE NULL END
      )`,
      [sessionId, userId, state]
    );

    const tokenExpiresAt =
      state === 'ABSOLUTE_EXPIRED'
        ? "NOW() - INTERVAL '3 hours'"
        : state === 'IDLE_EXPIRED' || options.tokenExpired
          ? "NOW() - INTERVAL '2 minutes'"
          : "NOW() + INTERVAL '1 day'";
    await pool.query(
      `INSERT INTO identity.refresh_tokens (id, session_id, secret_hash, expires_at)
       VALUES ($1, $2, $3, ${tokenExpiresAt})`,
      [tokenId, sessionId, hashRefreshTokenSecret(secret)]
    );

    if (options.consumed) {
      const successorId = generateUuidV7();
      await pool.query(
        'UPDATE identity.refresh_tokens SET revoked_at = NOW() WHERE id = $1',
        [tokenId]
      );
      await pool.query(
        `INSERT INTO identity.refresh_tokens (id, session_id, secret_hash, expires_at)
         VALUES ($1, $2, $3, NOW() + INTERVAL '1 day')`,
        [successorId, sessionId, hashRefreshTokenSecret(randomBytes(32))]
      );
      await pool.query(
        `UPDATE identity.refresh_tokens
         SET consumed_at = NOW() - INTERVAL '1 minute',
             revoked_at = NULL,
             replaced_by_token_id = $2
         WHERE id = $1`,
        [tokenId, successorId]
      );
    }

    return {
      userId,
      sessionId,
      tokenId,
      secret,
      credential: formatRefreshToken(tokenId, secret)
    };
  }

  afterAll(async () => {
    await prisma.$disconnect();
    await pool.end();
  });

  it('rotates once, persists only hashes, and slides idle expiry without crossing absolute expiry', async () => {
    const fixture = await createFixture();
    const before = await pool.query(
      'SELECT idle_expires_at, absolute_expires_at FROM identity.auth_sessions WHERE id = $1',
      [fixture.sessionId]
    );

    const result = await service.rotate(fixture.credential);
    const state = await pool.query(
      `SELECT rt.consumed_at, rt.replaced_by_token_id, successor.secret_hash,
              s.last_seen_at, s.idle_expires_at, s.absolute_expires_at
       FROM identity.refresh_tokens rt
       JOIN identity.refresh_tokens successor ON successor.id = rt.replaced_by_token_id
       JOIN identity.auth_sessions s ON s.id = rt.session_id
       WHERE rt.id = $1`,
      [fixture.tokenId]
    );

    expect(result.sessionId).toBe(fixture.sessionId);
    expect(result.refreshToken).not.toBe(fixture.credential);
    expect(result.refreshToken).toMatch(/^v1\./);
    expect(state.rows).toHaveLength(1);
    expect(state.rows[0].consumed_at).toBeTruthy();
    expect(state.rows[0].replaced_by_token_id).toBeTruthy();
    const parsedSuccessor = parseRefreshToken(result.refreshToken);
    if (!parsedSuccessor) throw new Error('Generated successor token did not parse');
    expect(state.rows[0].secret_hash).toBe(hashRefreshTokenSecret(parsedSuccessor.secret));
    expect(state.rows[0].secret_hash).not.toContain(parsedSuccessor.secret.toString('base64url'));
    expect(state.rows[0].idle_expires_at.getTime()).toBeGreaterThan(
      before.rows[0].idle_expires_at.getTime() - 1000
    );
    expect(state.rows[0].idle_expires_at.getTime()).toBeLessThanOrEqual(
      state.rows[0].absolute_expires_at.getTime()
    );
  });

  it('rejects a wrong secret without revoking the session', async () => {
    const fixture = await createFixture();
    const wrong = formatRefreshToken(fixture.tokenId, randomBytes(32));

    await expect(service.rotate(wrong)).rejects.toBeInstanceOf(UnauthorizedException);
    const session = await pool.query(
      'SELECT revoked_at FROM identity.auth_sessions WHERE id = $1',
      [fixture.sessionId]
    );
    expect(session.rows[0].revoked_at).toBeNull();
  });

  it('does not disclose unknown token identifiers', async () => {
    const unknown = formatRefreshToken(generateUuidV7(), randomBytes(32));
    await expect(service.rotate(unknown)).rejects.toBeInstanceOf(UnauthorizedException);
  });

  it('treats a valid consumed token as replay even after token expiry and revokes only its session', async () => {
    const fixture = await createFixture({ consumed: true, tokenExpired: true });
    const other = await createFixture();

    await expect(service.rotate(fixture.credential)).rejects.toBeInstanceOf(UnauthorizedException);
    const revoked = await pool.query(
      `SELECT revoked_at, revocation_reason FROM identity.auth_sessions WHERE id = $1`,
      [fixture.sessionId]
    );
    const family = await pool.query(
      'SELECT revoked_at FROM identity.refresh_tokens WHERE session_id = $1',
      [fixture.sessionId]
    );
    const otherSession = await pool.query(
      'SELECT revoked_at FROM identity.auth_sessions WHERE id = $1',
      [other.sessionId]
    );

    expect(revoked.rows[0].revocation_reason).toBe('REFRESH_REPLAY');
    expect(revoked.rows[0].revoked_at).toBeTruthy();
    expect(family.rows.every((row) => row.revoked_at !== null)).toBe(true);
    expect(otherSession.rows[0].revoked_at).toBeNull();
  });

  it.each(['IDLE_EXPIRED', 'ABSOLUTE_EXPIRED', 'REVOKED'] as const)(
    'rejects a %s session without rotating or reviving it',
    async (sessionState) => {
      const fixture = await createFixture({ sessionState });
      await expect(service.rotate(fixture.credential)).rejects.toBeInstanceOf(
        UnauthorizedException
      );
      const token = await pool.query(
        'SELECT consumed_at, revoked_at FROM identity.refresh_tokens WHERE id = $1',
        [fixture.tokenId]
      );
      expect(token.rows[0].consumed_at).toBeNull();
      expect(token.rows[0].revoked_at).toBeNull();
    }
  );

  it('rejects an expired active token', async () => {
    const fixture = await createFixture({ tokenExpired: true });
    await expect(service.rotate(fixture.credential)).rejects.toBeInstanceOf(
      UnauthorizedException
    );
  });

  it('rejects refresh when the owning user is inactive', async () => {
    const fixture = await createFixture({ userActive: false });
    await expect(service.rotate(fixture.credential)).rejects.toBeInstanceOf(
      UnauthorizedException
    );
  });

  it('serializes concurrent use: one rotation wins and the duplicate triggers strict replay revocation', async () => {
    const fixture = await createFixture();
    const results = await Promise.allSettled([
      service.rotate(fixture.credential),
      service.rotate(fixture.credential)
    ]);

    expect(results.filter((result) => result.status === 'fulfilled')).toHaveLength(1);
    expect(results.filter((result) => result.status === 'rejected')).toHaveLength(1);

    const session = await pool.query(
      'SELECT revoked_at, revocation_reason FROM identity.auth_sessions WHERE id = $1',
      [fixture.sessionId]
    );
    const tokens = await pool.query(
      'SELECT consumed_at, revoked_at FROM identity.refresh_tokens WHERE session_id = $1',
      [fixture.sessionId]
    );
    expect(session.rows[0].revocation_reason).toBe('REFRESH_REPLAY');
    expect(tokens.rows).toHaveLength(2);
    expect(tokens.rows.filter((row) => row.consumed_at !== null)).toHaveLength(1);
    expect(tokens.rows.every((row) => row.revoked_at !== null || row.consumed_at !== null)).toBe(
      true
    );
  });

  it('rolls back predecessor claim and session activity when successor insertion fails', async () => {
    const fixture = await createFixture();
    const triggerName = `fail_refresh_successor_${fixture.sessionId.replaceAll('-', '')}`;
    const functionName = `fail_refresh_successor_fn_${fixture.sessionId.replaceAll('-', '')}`;

    await pool.query(`
      CREATE FUNCTION public."${functionName}"() RETURNS trigger
      LANGUAGE plpgsql AS $$
      BEGIN
        IF NEW.session_id = '${fixture.sessionId}'::uuid THEN
          RAISE EXCEPTION 'intentional successor insert failure';
        END IF;
        RETURN NEW;
      END;
      $$;
    `);
    await pool.query(`
      CREATE TRIGGER "${triggerName}"
      BEFORE INSERT ON identity.refresh_tokens
      FOR EACH ROW EXECUTE FUNCTION public."${functionName}"()
    `);

    try {
      await expect(service.rotate(fixture.credential)).rejects.toThrow(
        'intentional successor insert failure'
      );
    } finally {
      await pool.query(`DROP TRIGGER IF EXISTS "${triggerName}" ON identity.refresh_tokens`);
      await pool.query(`DROP FUNCTION IF EXISTS public."${functionName}"()`);
    }

    const state = await pool.query(
      `SELECT rt.consumed_at, rt.revoked_at, s.last_seen_at, s.idle_expires_at
       FROM identity.refresh_tokens rt
       JOIN identity.auth_sessions s ON s.id = rt.session_id
       WHERE rt.id = $1`,
      [fixture.tokenId]
    );
    expect(state.rows[0].consumed_at).toBeNull();
    expect(state.rows[0].revoked_at).toBeNull();
  });
});
