import { Writable } from 'node:stream';
import type { Logger } from 'pino';
import { createAppLogger } from './logger.factory';

describe('createAppLogger', () => {
  it('redacts authentication secrets and sensitive request headers', async () => {
    const chunks: string[] = [];
    const destination = new Writable({
      write(chunk, _encoding, callback) {
        chunks.push(String(chunk));
        callback();
      }
    });
    const logger: Logger = createAppLogger('info', destination);

    logger.info({
      password: 'plain-password',
      otp: '123456',
      accessToken: 'access-secret',
      refreshToken: 'refresh-secret',
      csrfToken: 'csrf-secret',
      totpSecret: 'totp-secret',
      recoveryCode: 'recovery-secret',
      protectedPayload: 'encrypted-payload',
      req: { headers: {
        authorization: 'Bearer access-secret',
        cookie: 'session=refresh-secret'
      }}
    }, 'redaction-test');

    await new Promise<void>((resolve) => setImmediate(resolve));
    const output = chunks.join('');

    for (const secret of [
      'plain-password', '123456', 'access-secret', 'refresh-secret',
      'csrf-secret', 'totp-secret', 'recovery-secret', 'encrypted-payload',
      'session='
    ]) {
      expect(output).not.toContain(secret);
    }

    expect(output).toContain('[REDACTED]');
  });
});
