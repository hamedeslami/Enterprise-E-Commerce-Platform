import { randomBytes } from 'node:crypto';

const MAX_UUID_TIMESTAMP = 0xffffffffffff;

export function generateUuidV7(now = Date.now()): string {
  if (!Number.isSafeInteger(now) || now < 0 || now > MAX_UUID_TIMESTAMP) {
    throw new RangeError('UUIDv7 timestamp must be a valid 48-bit millisecond value.');
  }

  const bytes = randomBytes(16);
  let timestamp = now;

  for (let index = 5; index >= 0; index -= 1) {
    bytes[index] = timestamp & 0xff;
    timestamp = Math.floor(timestamp / 256);
  }

  bytes[6] = 0x70 | (bytes[6]! & 0x0f);
  bytes[8] = 0x80 | (bytes[8]! & 0x3f);

  const hex = bytes.toString('hex');
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20)
  ].join('-');
}
