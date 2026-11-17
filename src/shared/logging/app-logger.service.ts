import { Inject, Injectable, LoggerService } from '@nestjs/common';
import type { Logger } from 'pino';

export const APP_LOGGER = Symbol('APP_LOGGER');

@Injectable()
export class AppLogger implements LoggerService {
  constructor(@Inject(APP_LOGGER) private readonly logger: Logger) {}

  log(message: string, context?: string): void {
    this.logger.info({ context }, message);
  }

  error(message: string, stack?: string, context?: string): void {
    this.logger.error({ context, stack }, message);
  }

  warn(message: string, context?: string): void {
    this.logger.warn({ context }, message);
  }

  debug(message: string, context?: string): void {
    this.logger.debug({ context }, message);
  }

  verbose(message: string, context?: string): void {
    this.logger.trace({ context }, message);
  }

  fatal(message: string, context?: string): void {
    this.logger.fatal({ context }, message);
  }
}
