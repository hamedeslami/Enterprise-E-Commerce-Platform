import { Injectable, UnauthorizedException } from '@nestjs/common';
import { generateUuidV7 } from '../../../shared/identifiers/uuidv7.js';
import { PrismaService } from '../../../shared/database/prisma.service.js';
import {
  createRefreshTokenSecret,
  formatRefreshToken,
  hashRefreshTokenSecret,
  parseRefreshToken,
  verifyRefreshTokenSecret
} from './refresh-token.crypto.js';

interface TokenOwnerRow { session_id: string; user_id: string; secret_hash: string; }
interface UserLockRow { id: string; is_active: boolean; }
interface SessionLockRow {
  id: string; user_id: string; revoked_at: Date | null;
  idle_expires_at: Date; absolute_expires_at: Date;
}
interface TokenLockRow {
  id: string; session_id: string; secret_hash: string; expires_at: Date;
  consumed_at: Date | null; revoked_at: Date | null;
}
interface DatabaseClockRow {
  database_now: Date;
  session_within_lifetime: boolean;
  token_unexpired: boolean;
}
interface SuccessorExpiryRow { expires_at: Date; }

type RotationTransactionResult =
  | { status: 'rejected' }
  | { status: 'replay' }
  | { status: 'rotated'; sessionId: string; refreshToken: string; expiresAt: Date };

@Injectable()
export class RefreshTokenService {
  constructor(private readonly prisma: PrismaService) {}

  async rotate(presentedCredential: string): Promise<{
    sessionId: string;
    refreshToken: string;
    expiresAt: Date;
  }> {
    const parsed = parseRefreshToken(presentedCredential);
    if (!parsed) throw this.unauthorized();

    // Unlocked lookup is only a routing/pre-filter step. Security-relevant values
    // are re-read after acquiring the prescribed row locks.
    const owners = await this.prisma.$queryRaw<TokenOwnerRow[]>`
      SELECT rt.session_id, s.user_id, rt.secret_hash
      FROM identity.refresh_tokens AS rt
      INNER JOIN identity.auth_sessions AS s ON s.id = rt.session_id
      WHERE rt.id = ${parsed.tokenId}::uuid
      LIMIT 1
    `;
    const owner = owners[0];
    if (!owner || !verifyRefreshTokenSecret(parsed.secret, owner.secret_hash)) {
      throw this.unauthorized();
    }

    const result = await this.prisma.$transaction<RotationTransactionResult>(async (tx) => {
      // Global lock order for session mutations: User -> Session -> RefreshToken.
      const users = await tx.$queryRaw<UserLockRow[]>`
        SELECT id, is_active FROM identity.users
        WHERE id = ${owner.user_id}::uuid
        FOR UPDATE
      `;
      const user = users[0];
      if (!user || !user.is_active) return { status: 'rejected' };

      const sessions = await tx.$queryRaw<SessionLockRow[]>`
        SELECT id, user_id, revoked_at, idle_expires_at, absolute_expires_at
        FROM identity.auth_sessions
        WHERE id = ${owner.session_id}::uuid AND user_id = ${owner.user_id}::uuid
        FOR UPDATE
      `;
      const session = sessions[0];
      if (!session) return { status: 'rejected' };

      const tokens = await tx.$queryRaw<TokenLockRow[]>`
        SELECT id, session_id, secret_hash, expires_at, consumed_at, revoked_at
        FROM identity.refresh_tokens
        WHERE id = ${parsed.tokenId}::uuid AND session_id = ${owner.session_id}::uuid
        FOR UPDATE
      `;
      const token = tokens[0];
      if (
        !token ||
        !verifyRefreshTokenSecret(parsed.secret, token.secret_hash) ||
        token.session_id !== session.id
      ) {
        return { status: 'rejected' };
      }

      const clocks = await tx.$queryRaw<DatabaseClockRow[]>`
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS database_now)
        SELECT clock.database_now,
               s.idle_expires_at > clock.database_now
                 AND s.absolute_expires_at > clock.database_now AS session_within_lifetime,
               rt.expires_at > clock.database_now AS token_unexpired
        FROM clock
        INNER JOIN identity.auth_sessions AS s ON s.id = ${session.id}::uuid
        INNER JOIN identity.refresh_tokens AS rt ON rt.id = ${token.id}::uuid
      `;
      const clock = clocks[0];
      const databaseNow = clock.database_now;

      if (session.revoked_at !== null || !clock.session_within_lifetime) {
        return { status: 'rejected' };
      }

      // A consumed credential is checked before its own expiry, but only while
      // the owning session remains active.
      if (token.consumed_at !== null) {
        await tx.$executeRaw`
          UPDATE identity.auth_sessions
          SET revoked_at = ${databaseNow},
              revocation_reason = 'REFRESH_REPLAY'::identity."SessionRevocationReason"
          WHERE id = ${session.id}::uuid AND revoked_at IS NULL
        `;
        await tx.$executeRaw`
          UPDATE identity.refresh_tokens
          SET revoked_at = ${databaseNow}
          WHERE session_id = ${session.id}::uuid
            AND consumed_at IS NULL AND revoked_at IS NULL
        `;
        return { status: 'replay' };
      }

      if (token.revoked_at !== null || !clock.token_unexpired) {
        return { status: 'rejected' };
      }

      // Temporarily revoke the predecessor to free the partial unique index.
      // The transaction inserts the successor and then finalizes the predecessor
      // as consumed+replaced. No intermediate state can be committed.
      const claimed = await tx.$queryRaw<Array<{ id: string }>>`
        UPDATE identity.refresh_tokens AS rt
        SET revoked_at = ${databaseNow}
        WHERE rt.id = ${token.id}::uuid
          AND rt.session_id = ${session.id}::uuid
          AND rt.consumed_at IS NULL AND rt.revoked_at IS NULL
          AND rt.expires_at > ${databaseNow}
          AND EXISTS (
            SELECT 1 FROM identity.auth_sessions AS s
            WHERE s.id = rt.session_id AND s.revoked_at IS NULL
              AND s.idle_expires_at > ${databaseNow}
              AND s.absolute_expires_at > ${databaseNow}
          )
        RETURNING rt.id
      `;
      if (claimed.length !== 1) return { status: 'rejected' };

      const updatedSessions = await tx.$queryRaw<Array<{ id: string }>>`
        UPDATE identity.auth_sessions
        SET last_seen_at = ${databaseNow},
            idle_expires_at = LEAST(
              ${databaseNow}::timestamptz + (idle_expires_at - last_seen_at),
              absolute_expires_at
            )
        WHERE id = ${session.id}::uuid AND revoked_at IS NULL
          AND idle_expires_at > last_seen_at
          AND idle_expires_at > ${databaseNow}
          AND absolute_expires_at > ${databaseNow}
        RETURNING id
      `;
      const updatedSession = updatedSessions[0];
      if (!updatedSession) {
        throw new Error('Refresh token session update invariant failed');
      }

      const successorId = generateUuidV7();
      const successorSecret = createRefreshTokenSecret();
      const successorHash = hashRefreshTokenSecret(successorSecret);

      const successors = await tx.$queryRaw<SuccessorExpiryRow[]>`
        INSERT INTO identity.refresh_tokens (id, session_id, secret_hash, expires_at)
        SELECT ${successorId}::uuid, s.id, ${successorHash}, s.idle_expires_at
        FROM identity.auth_sessions AS s
        WHERE s.id = ${session.id}::uuid
        RETURNING expires_at
      `;
      const successor = successors[0];
      if (!successor) throw new Error('Refresh token successor insertion invariant failed');

      const finalized = await tx.$queryRaw<Array<{ id: string }>>`
        UPDATE identity.refresh_tokens
        SET consumed_at = ${databaseNow},
            revoked_at = NULL,
            replaced_by_token_id = ${successorId}::uuid
        WHERE id = ${token.id}::uuid AND session_id = ${session.id}::uuid
          AND consumed_at IS NULL AND revoked_at IS NOT NULL
        RETURNING id
      `;
      if (finalized.length !== 1) {
        throw new Error('Refresh token predecessor finalization invariant failed');
      }

      return {
        status: 'rotated',
        sessionId: session.id,
        refreshToken: formatRefreshToken(successorId, successorSecret),
        expiresAt: successor.expires_at
      };
    });

    if (result.status !== 'rotated') throw this.unauthorized();
    return {
      sessionId: result.sessionId,
      refreshToken: result.refreshToken,
      expiresAt: result.expiresAt
    };
  }

  private unauthorized(): UnauthorizedException {
    return new UnauthorizedException('Invalid refresh credential');
  }
}
