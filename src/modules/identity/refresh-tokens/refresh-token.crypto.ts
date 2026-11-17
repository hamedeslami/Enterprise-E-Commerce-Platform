import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';

const TOKEN_PATTERN = /^v1\.([0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})\.([A-Za-z0-9_-]{43})$/;

export interface ParsedRefreshToken {
  tokenId: string;
  secret: Buffer;
}

export function parseRefreshToken(value: string): ParsedRefreshToken | null {
  if (typeof value !== 'string' || value.length > 128) return null;

  const match = TOKEN_PATTERN.exec(value);
  if (!match) return null;

  const secret = Buffer.from(match[2], 'base64url');
  if (secret.length !== 32 || secret.toString('base64url') !== match[2]) return null;

  return { tokenId: match[1], secret };
}

export function createRefreshTokenSecret(): Buffer {
  return randomBytes(32);
}

export function hashRefreshTokenSecret(secret: Buffer): string {
  return createHash('sha256').update(secret).digest('hex');
}

export function verifyRefreshTokenSecret(secret: Buffer, expectedHash: string): boolean {
  if (!/^[0-9a-f]{64}$/.test(expectedHash)) return false;

  const actual = createHash('sha256').update(secret).digest();
  const expected = Buffer.from(expectedHash, 'hex');

  return expected.length === actual.length && timingSafeEqual(actual, expected);
}

export function formatRefreshToken(tokenId: string, secret: Buffer): string {
  if (secret.length !== 32) throw new Error('Refresh token secret must contain 256 bits');
  return `v1.${tokenId}.${secret.toString('base64url')}`;
}
