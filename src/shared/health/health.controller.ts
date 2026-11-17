import { Controller, Get } from '@nestjs/common';
import {
  ApiOkResponse,
  ApiServiceUnavailableResponse,
  ApiTags
} from '@nestjs/swagger';
import { HealthService } from './health.service.js';

@ApiTags('health')
@Controller('health')
export class HealthController {
  constructor(private readonly healthService: HealthService) {}

  @Get('live')
  @ApiOkResponse({ description: 'The process is running.' })
  checkLiveness(): { status: 'ok' } {
    return { status: 'ok' };
  }

  @Get('ready')
  @ApiOkResponse({ description: 'The application can reach PostgreSQL.' })
  @ApiServiceUnavailableResponse({ description: 'A required dependency is unavailable.' })
  checkReadiness(): Promise<{ status: 'ok'; database: 'ok' }> {
    return this.healthService.checkReadiness();
  }
}
