import { randomUUID } from 'node:crypto'
import type { Request, Response } from 'express'
import type { AuthErrorCode, CanonicalApiError } from '../../src/types/index.js'

export type CanonicalAuthorizationErrorCode = AuthErrorCode
  | 'invalid_scope'
  | 'forbidden'
  | 'not_found'
  | 'version_conflict'

export interface CanonicalAuthorizationError {
  code: CanonicalAuthorizationErrorCode
  message: string
  statusCode: number
}

export interface AuthorizationErrorFallback {
  code: CanonicalAuthorizationErrorCode
  statusCode: number
  message?: string
}

const errorDefaults: Record<CanonicalAuthorizationErrorCode, Omit<CanonicalAuthorizationError, 'code'>> = {
  auth_required: { statusCode: 401, message: 'Authentication is required.' },
  invalid_token: { statusCode: 401, message: 'The access token is invalid.' },
  wrong_tenant: { statusCode: 401, message: 'The access token tenant is not authorized.' },
  invalid_audience: { statusCode: 401, message: 'The access token audience is not authorized.' },
  unauthorized_client: { statusCode: 401, message: 'The calling client is not authorized.' },
  token_stale: { statusCode: 401, message: 'The access token must be refreshed.' },
  role_missing: { statusCode: 403, message: 'The required application role is missing.' },
  role_conflict: { statusCode: 403, message: 'The access token contains conflicting roles.' },
  assignment_missing: { statusCode: 403, message: 'No active role assignment was found.' },
  assignment_revoked: { statusCode: 403, message: 'The role assignment is no longer active.' },
  scope_unmapped: { statusCode: 403, message: 'The assigned scope is not configured.' },
  membership_missing: { statusCode: 403, message: 'An active scope membership is required.' },
  invalid_job_scope: { statusCode: 403, message: 'The requested job scope is not authorized.' },
  identity_disabled: { statusCode: 403, message: 'The user identity is disabled.' },
  invalid_navigation_action: { statusCode: 400, message: 'The navigation request is invalid. Try again.' },
  navigation_audit_unavailable: { statusCode: 503, message: 'The navigation change could not be recorded. Try again.' },
  invalid_scope: { statusCode: 400, message: 'The requested scope is invalid.' },
  forbidden: { statusCode: 403, message: 'Access is forbidden.' },
  not_found: { statusCode: 404, message: 'The requested resource was not found.' },
  version_conflict: { statusCode: 409, message: 'Authorization state changed. Refresh and retry.' },
}

const canonicalCodes = new Set<CanonicalAuthorizationErrorCode>(
  Object.keys(errorDefaults) as CanonicalAuthorizationErrorCode[],
)

function errorCode(value: unknown): CanonicalAuthorizationErrorCode | undefined {
  if (typeof value !== 'string') return undefined
  const normalized = value.trim().toLowerCase()
  if (normalized === 'conflict') return 'version_conflict'
  return canonicalCodes.has(normalized as CanonicalAuthorizationErrorCode)
    ? normalized as CanonicalAuthorizationErrorCode
    : undefined
}

export function getAuthorizationErrorMessage(code: CanonicalAuthorizationErrorCode): string {
  return errorDefaults[code].message
}

export function mapAuthorizationError(
  error: unknown,
  fallback: AuthorizationErrorFallback,
  options: { preserveMessage?: boolean } = {},
): CanonicalAuthorizationError {
  const candidate = error && typeof error === 'object'
    ? error as { code?: unknown, error?: unknown, message?: unknown, statusCode?: unknown }
    : undefined
  const candidateCode = errorCode(candidate?.code ?? candidate?.error)
  const code = candidateCode ?? fallback.code
  const statusCode = typeof candidate?.statusCode === 'number'
    && Number.isInteger(candidate.statusCode)
    && candidate.statusCode >= 400
    && candidate.statusCode <= 599
    ? candidate.statusCode
    : fallback.statusCode
  const candidateMessage = candidateCode
    && options.preserveMessage !== false
    && typeof candidate?.message === 'string'
    ? candidate.message.trim()
    : ''

  return {
    code,
    statusCode,
    message: candidateMessage || fallback.message || getAuthorizationErrorMessage(code),
  }
}

export function ensureCorrelationId(req: Request, res: Response): string {
  const current = res.getHeader('X-Correlation-ID')
  const existing = Array.isArray(current) ? current[0] : current
  const correlationId = existing === undefined || String(existing).trim() === ''
    ? req.get('X-Correlation-ID')?.trim() || randomUUID()
    : String(existing)
  res.setHeader('X-Correlation-ID', correlationId)
  return correlationId
}

export function sendAuthorizationError(
  req: Request,
  res: Response,
  error: CanonicalAuthorizationError,
): Response<CanonicalApiError> {
  const correlationId = ensureCorrelationId(req, res)
  return res.status(error.statusCode).json({
    error: error.code,
    message: error.message,
    correlationId,
  })
}