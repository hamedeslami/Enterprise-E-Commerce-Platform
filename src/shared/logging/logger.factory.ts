import { requestContext } from '../request-context/request-context';
import pino, {
  type DestinationStream,
  type Logger,
  type LoggerOptions
} from 'pino';

export const SENSITIVE_LOG_PATHS = [
  'password', '*.password',
  'otp', '*.otp',
  'accessToken', '*.accessToken',
  'refreshToken', '*.refreshToken',
  'csrfToken', '*.csrfToken',
  'totpSecret', '*.totpSecret',
  'recoveryCode', '*.recoveryCode',
  'protectedPayload', '*.protectedPayload',
  'authorization', '*.authorization',
  'cookie', '*.cookie',
  'set-cookie', '*.set-cookie',
  'req.headers.authorization',
  'req.headers.cookie',
  'res.headers.set-cookie'
];

export function createAppLogger(level = 'info', destination?: DestinationStream): Logger {
  const options: LoggerOptions = {
    level,
    timestamp: pino.stdTimeFunctions.isoTime,
    redact: { paths: SENSITIVE_LOG_PATHS, censor: '[REDACTED]' },
    mixin() {
      const context = requestContext.getStore();
      return context ? { requestId: context.requestId } : {};
    }
  };

  return destination ? pino(options, destination) : pino(options);
}
