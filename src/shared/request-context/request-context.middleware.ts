import { Injectable, NestMiddleware } from '@nestjs/common';
import { randomUUID } from 'node:crypto';
import type { NextFunction, Request, Response } from 'express';
import { requestContext } from './request-context';

@Injectable()
export class RequestContextMiddleware implements NestMiddleware {
  use(_request: Request, response: Response, next: NextFunction): void {
    const requestId = randomUUID();
    response.setHeader('X-Request-Id', requestId);
    requestContext.run({ requestId }, next);
  }
}
