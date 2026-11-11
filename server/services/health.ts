import { getPool, isAzureSql } from '../storage/db.js'

export type DependencyStatus = 'ok' | 'failed' | 'skipped'

export interface DependencyCheck {
  name: string
  status: DependencyStatus
  target?: string
  latencyMs?: number
  detail?: string
}

export interface HealthReport {
  status: 'ok' | 'unhealthy'
  stack: 'stack-a'
  storage: 'azure-sql' | 'sqlite'
  timestamp: string
  checks: {
    awrApi: DependencyCheck
    azureSql: DependencyCheck
  }
}

export function resolveAwrHealthUrl(endpoint: string | undefined): string | null {
  if (!endpoint?.trim()) {
    return null
  }

  try {
    return new URL('/healthz', endpoint).toString()
  } catch {
    return null
  }
}

export function summarizeHealth(checks: DependencyCheck[]): 'ok' | 'unhealthy' {
  return checks.some(check => check.status === 'failed') ? 'unhealthy' : 'ok'
}

export async function buildHealthReport(): Promise<HealthReport> {
  const awrApi = await checkAwrApiHealth()
  const azureSql = await checkAzureSqlHealth()

  return {
    status: summarizeHealth([awrApi, azureSql]),
    stack: 'stack-a',
    storage: isAzureSql ? 'azure-sql' : 'sqlite',
    timestamp: new Date().toISOString(),
    checks: {
      awrApi,
      azureSql,
    },
  }
}

async function checkAwrApiHealth(): Promise<DependencyCheck> {
  const target = resolveAwrHealthUrl(process.env.AWR_SEQ_API_ENDPOINT)
  if (!target) {
    return {
      name: 'awr-api',
      status: process.env.AWR_SEQ_API_ENDPOINT ? 'failed' : 'skipped',
      detail: process.env.AWR_SEQ_API_ENDPOINT
        ? 'AWR_SEQ_API_ENDPOINT is not a valid URL.'
        : 'AWR_SEQ_API_ENDPOINT is not configured.',
    }
  }

  const startedAt = Date.now()
  try {
    const response = await fetchWithTimeout(target, 5000)
    const latencyMs = Date.now() - startedAt

    if (!response.ok) {
      return {
        name: 'awr-api',
        status: 'failed',
        target,
        latencyMs,
        detail: `Health endpoint returned HTTP ${response.status}.`,
      }
    }

    return {
      name: 'awr-api',
      status: 'ok',
      target,
      latencyMs,
    }
  } catch (error) {
    return {
      name: 'awr-api',
      status: 'failed',
      target,
      latencyMs: Date.now() - startedAt,
      detail: normalizeErrorMessage(error),
    }
  }
}

async function checkAzureSqlHealth(): Promise<DependencyCheck> {
  if (!isAzureSql) {
    return {
      name: 'azure-sql',
      status: 'skipped',
      detail: 'Azure SQL is not enabled for this environment.',
    }
  }

  const startedAt = Date.now()
  try {
    const pool = await getPool()
    await pool.request().query('SELECT 1 AS HealthCheck')

    return {
      name: 'azure-sql',
      status: 'ok',
      target: process.env.AZURE_SQL_SERVER_FQDN,
      latencyMs: Date.now() - startedAt,
    }
  } catch (error) {
    return {
      name: 'azure-sql',
      status: 'failed',
      target: process.env.AZURE_SQL_SERVER_FQDN,
      latencyMs: Date.now() - startedAt,
      detail: normalizeErrorMessage(error),
    }
  }
}

async function fetchWithTimeout(url: string, timeoutMs: number): Promise<Response> {
  const controller = new AbortController()
  const timeoutId = setTimeout(() => controller.abort(), timeoutMs)

  try {
    return await fetch(url, {
      method: 'GET',
      signal: controller.signal,
    })
  } finally {
    clearTimeout(timeoutId)
  }
}

function normalizeErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message
  }

  return String(error)
}
