import { MiddlewareConsumer, Module, NestModule } from '@nestjs/common';
import { DatabaseModule } from './shared/database/database.module.js';
import { HealthController } from './shared/health/health.controller.js';
import { HealthService } from './shared/health/health.service.js';
import { LoggingModule } from './shared/logging/logging.module.js';
import { RequestContextMiddleware } from './shared/request-context/request-context.middleware.js';

@Module({
  imports: [DatabaseModule, LoggingModule],
  controllers: [HealthController],
  providers: [HealthService, RequestContextMiddleware]
})
export class AppModule implements NestModule {
  configure(consumer: MiddlewareConsumer): void {
    consumer.apply(RequestContextMiddleware).forRoutes('*');
  }
}
