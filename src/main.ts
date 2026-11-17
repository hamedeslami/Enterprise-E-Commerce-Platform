import 'dotenv/config';
import 'reflect-metadata';
import { NestFactory } from '@nestjs/core';
import { AppModule } from './app.module.js';
import { loadEnvironment } from './shared/config/environment.js';
import { ProblemDetailsFilter } from './shared/http/problem-details.filter.js';
import { configureOpenApi } from './shared/http/openapi.js';
import { AppLogger } from './shared/logging/app-logger.service.js';

async function bootstrap(): Promise<void> {
  const environment = loadEnvironment();
  const app = await NestFactory.create(AppModule, { bufferLogs: true });

  app.useLogger(app.get(AppLogger));
  app.useGlobalFilters(new ProblemDetailsFilter(app.get(AppLogger)));
  configureOpenApi(app);

  app.enableShutdownHooks();
  await app.listen(environment.PORT, '0.0.0.0');
}

void bootstrap();
