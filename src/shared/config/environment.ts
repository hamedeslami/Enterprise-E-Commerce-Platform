import { z } from 'zod';

const EnvironmentSchema = z.object({
  NODE_ENV: z.enum(['development', 'test', 'production']).default('development'),
  PORT: z.coerce.number().int().min(1).max(65535),
  DATABASE_URL: z.string().url().refine(
    (value) => value.startsWith('postgresql://') || value.startsWith('postgres://'),
    'DATABASE_URL must use the PostgreSQL protocol'
  )
});

export type AppEnvironment = z.infer<typeof EnvironmentSchema>;

export function loadEnvironment(source: NodeJS.ProcessEnv = process.env): AppEnvironment {
  const result = EnvironmentSchema.safeParse({
    ...source,
    PORT: source.PORT ?? '8080'
  });

  if (!result.success) {
    const invalidKeys = result.error.issues
      .map((issue) => issue.path.join('.'))
      .filter(Boolean)
      .join(', ');
    throw new Error(
      `Invalid environment configuration. Check: ${invalidKeys || 'environment variables'}`
    );
  }

  return result.data;
}
