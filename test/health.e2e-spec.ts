import { INestApplication } from '@nestjs/common';
import { Test } from '@nestjs/testing';
import request from 'supertest';
import { AppModule } from '../src/app.module.js';
import { configureOpenApi } from '../src/shared/http/openapi.js';

describe('Health endpoints (e2e)', () => {
  let app: INestApplication;

  beforeAll(async () => {
    process.env.NODE_ENV = 'test';
    const moduleRef = await Test.createTestingModule({
      imports: [AppModule]
    }).compile();

    app = moduleRef.createNestApplication();
    configureOpenApi(app);
    await app.init();
  });

  afterAll(async () => {
    await app.close();
  });

  it('returns liveness without requiring a database query', async () => {
    const response = await request(app.getHttpServer())
      .get('/health/live')
      .expect(200);

    expect(response.body).toEqual({ status: 'ok' });
    expect(response.headers['x-request-id']).toBeDefined();
  });

  it('returns readiness after querying the real PostgreSQL service', async () => {
    const response = await request(app.getHttpServer())
      .get('/health/ready')
      .expect(200);

    expect(response.body).toEqual({ status: 'ok', database: 'ok' });
    expect(response.headers['content-type']).toContain('application/json');
  });

  it('publishes the OpenAPI document', async () => {
    const response = await request(app.getHttpServer())
      .get('/openapi/v1.json')
      .expect(200);

    expect(response.body.openapi).toMatch(/^3\\./);
    expect(response.body.paths['/health/live']).toBeDefined();
  });
});
