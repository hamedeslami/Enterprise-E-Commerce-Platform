import {
  ArgumentsHost,
  Catch,
  ExceptionFilter,
  HttpException,
  HttpStatus
} from '@nestjs/common';
import type { Request, Response } from 'express';
import { AppLogger } from '../logging/app-logger.service';
import { requestContext } from '../request-context/request-context';

@Catch()
export class ProblemDetailsFilter implements ExceptionFilter {
  constructor(private readonly logger: AppLogger) {}

  catch(exception: unknown, host: ArgumentsHost): void {
    const http = host.switchToHttp();
    const response = http.getResponse<Response>();
    const request = http.getRequest<Request>();
    const status = exception instanceof HttpException
      ? exception.getStatus()
      : HttpStatus.INTERNAL_SERVER_ERROR;
    const requestId = requestContext.getStore()?.requestId;
    const detail = status >= 500
      ? 'An unexpected error occurred.'
      : exception instanceof HttpException
        ? this.getSafeDetail(exception)
        : 'The request could not be completed.';

    if (status >= 500) {
      this.logger.error(
        `Unhandled request failure (status ${status})`,
        undefined,
        ProblemDetailsFilter.name
      );
    }

    response.status(status).type('application/problem+json').json({
      type: 'about:blank',
      title: HttpStatus[status] ?? 'HTTP Error',
      status,
      detail,
      instance: request.url,
      ...(requestId ? { requestId } : {})
    });
  }

  private getSafeDetail(exception: HttpException): string {
    const payload = exception.getResponse();

    if (typeof payload === 'string') return payload;
    if (
      typeof payload === 'object' &&
      payload !== null &&
      'message' in payload &&
      typeof payload.message === 'string'
    ) return payload.message;

    return 'The request could not be completed.';
  }
}
