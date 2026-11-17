import { Global, Module } from '@nestjs/common';
import { AppLogger, APP_LOGGER } from './app-logger.service.js';
import { createAppLogger } from './logger.factory.js';

@Global()
@Module({
  providers: [
    {
      provide: APP_LOGGER,
      useFactory: () =>
        createAppLogger(process.env.NODE_ENV === 'production' ? 'info' : 'debug')
    },
    AppLogger
  ],
  exports: [AppLogger]
})
export class LoggingModule {}
