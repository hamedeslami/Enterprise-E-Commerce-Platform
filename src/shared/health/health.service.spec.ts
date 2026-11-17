import { jest } from '@jest/globals';
import { ServiceUnavailableException } from '@nestjs/common';
import { HealthService } from './health.service.js';

describe('HealthService', () => {
  it('reports readiness only after a successful database query', async () => {
    const prisma = {
      $queryRaw: jest.fn().mockResolvedValue([{ '?column?': 1 }])
    };
    const service = new HealthService(prisma as never);

    await expect(service.checkReadiness()).resolves.toEqual({
      status: 'ok',
      database: 'ok'
    });
    expect(prisma.$queryRaw).toHaveBeenCalledTimes(1);
  });

  it('fails readiness when PostgreSQL is unavailable', async () => {
    const prisma = {
      $queryRaw: jest.fn().mockRejectedValue(new Error('connection refused'))
    };
    const service = new HealthService(prisma as never);

    await expect(service.checkReadiness()).rejects.toBeInstanceOf(
      ServiceUnavailableException
    );
  });
});
