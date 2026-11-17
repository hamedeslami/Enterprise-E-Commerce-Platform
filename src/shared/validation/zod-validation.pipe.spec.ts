import { BadRequestException } from '@nestjs/common';
import { z } from 'zod';
import { ZodValidationPipe } from './zod-validation.pipe.js';

describe('ZodValidationPipe', () => {
  it('returns parsed and transformed values', () => {
    const pipe = new ZodValidationPipe(
      z.object({ quantity: z.coerce.number().int().positive() })
    );

    expect(pipe.transform({ quantity: '3' })).toEqual({ quantity: 3 });
  });

  it('rejects invalid input with safe validation details', () => {
    const pipe = new ZodValidationPipe(z.object({ email: z.string().email() }));

    expect(() => pipe.transform({ email: 'invalid' })).toThrow(
      BadRequestException
    );
  });
});
