import 'reflect-metadata';
import { NestFactory } from '@nestjs/core';
import { DocumentBuilder, SwaggerModule } from '@nestjs/swagger';
import { AppModule } from './app.module';
import { loadEnvironment } from './shared/config/environment';
import { ProblemDetailsFilter } from './shared/http/problem-details.filter';
import { AppLogger } from './shared/logging/app-logger.service';

async function bootstrap(): Promise<void> {
  const environment = loadEnvironment();
  const app = await NestFactory.create(AppModule, { bufferLogs: true });

  app.useLogger(app.get(AppLogger));
  app.useGlobalFilters(new ProblemDetailsFilter(app.get(AppLogger)));

  const openApiConfig = new DocumentBuilder()
    .setTitle('Enterprise E-Commerce API')
    .setDescription('Backend API foundation')
    .setVersion('1.0.0')
    .build();
  const openApiDocument = SwaggerModule.createDocument(app, openApiConfig);
  SwaggerModule.setup('docs', app, openApiDocument, {
    jsonDocumentUrl: 'openapi/v1.json'
  });

  app.enableShutdownHooks();
  await app.listen(environment.PORT, '0.0.0.0');
}

void bootstrap();
