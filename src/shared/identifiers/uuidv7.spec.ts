import { generateUuidV7 } from './uuidv7.js';

describe('generateUuidV7', () => {
  it('generates a UUID with version 7 and the RFC 4122 variant', () => {
    const value = generateUuidV7(1_800_000_000_123);

    expect(value).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  });

  it('encodes the supplied Unix millisecond timestamp in the first 48 bits', () => {
    const timestamp = 1_800_000_000_123;
    const value = generateUuidV7(timestamp);
    const encoded = BigInt(`0x${value.replaceAll('-', '').slice(0, 12)}`);

    expect(encoded).toBe(BigInt(timestamp));
  });

  it('rejects timestamps outside the UUIDv7 48-bit range', () => {
    expect(() => generateUuidV7(-1)).toThrow(RangeError);
    expect(() => generateUuidV7(0x1_0000_0000_0000)).toThrow(RangeError);
    expect(() => generateUuidV7(Number.NaN)).toThrow(RangeError);
  });
});
