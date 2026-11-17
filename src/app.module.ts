import { MiddlewareConsumer, Module, NestModule } from '@nestjs/common';
import { DatabaseModule } from './shared/database/database.module';
import { HealthController } from './shared/health/health.controller';
import { HealthService } from './shared/health/health.service';
import { LoggingModule } from './shared/logging/logging.module';
import { RequestContextMiddleware } from './shared/request-context/request-context.middleware';

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
