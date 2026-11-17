import {
  createRefreshTokenSecret,
  formatRefreshToken,
  hashRefreshTokenSecret,
  parseRefreshToken,
  verifyRefreshTokenSecret
} from './refresh-token.crypto.js';

describe('refresh token cryptography', () => {
  it('round-trips a versioned UUIDv7 token with a 256-bit secret', () => {
    const tokenId = '019b2f6a-7c10-7abc-8def-0123456789ab';
    const secret = createRefreshTokenSecret();
    const token = formatRefreshToken(tokenId, secret);
    const parsed = parseRefreshToken(token);

    expect(parsed?.tokenId).toBe(tokenId);
    expect(parsed?.secret.equals(secret)).toBe(true);
    expect(verifyRefreshTokenSecret(secret, hashRefreshTokenSecret(secret))).toBe(true);
  });

  it.each([
    '',
    'v1.not-a-uuid.secret',
    'v1.019b2f6a-7c10-4abc-8def-0123456789ab.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
    'v1.019b2f6a-7c10-7abc-0def-0123456789ab.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
    'v1.019b2f6a-7c10-7abc-8def-0123456789ab.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!',
    'v1.019b2f6a-7c10-7abc-8def-0123456789ab.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.extra'
  ])('rejects malformed token %s', (token) => {
    expect(parseRefreshToken(token)).toBeNull();
  });

  it('rejects a mismatched secret without treating it as a valid replay signal', () => {
    const expected = createRefreshTokenSecret();
    const presented = createRefreshTokenSecret();

    expect(
      verifyRefreshTokenSecret(presented, hashRefreshTokenSecret(expected))
    ).toBe(false);
  });

  it('rejects malformed persisted hashes', () => {
    expect(verifyRefreshTokenSecret(createRefreshTokenSecret(), 'not-a-hash')).toBe(false);
  });
});
