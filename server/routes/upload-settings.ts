import { Router, type Response } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { ensureCorrelationId } from '../services/authorization-errors.js'
import { optionalUploadService } from '../services/optional-upload.js'

function isGlobalAdmin(req: AuthenticatedRequest): boolean {
  if (req.authorizationContext) {
    return req.authorizationContext.globalRole === 'admin'
  }
  return req.user?.role === 'admin'
}

function errorResponse(
  req: AuthenticatedRequest,
  res: Response,
  error: unknown,
) {
  const candidate = error as {
    code?: string
    statusCode?: number
    message?: string
    errors?: Record<string, string[]>
  }
  const statusCode = Number.isInteger(candidate?.statusCode)
    ? candidate.statusCode!
    : 500
  const correlationId = ensureCorrelationId(req, res)
  return res.status(statusCode).json({
    error: candidate?.code ?? 'internal_error',
    message: statusCode === 500
      ? 'The optional upload settings operation could not be completed.'
      : candidate?.message ?? 'The optional upload settings operation failed.',
    correlationId,
    ...(candidate?.errors ? { errors: candidate.errors } : {}),
  })
}

export function createUploadSettingsRouter() {
  const router = Router()

  router.get('/', async (req: AuthenticatedRequest, res) => {
    ensureCorrelationId(req, res)
    if (!isGlobalAdmin(req)) {
      return res.status(403).json({
        error: 'forbidden',
        message: 'Only global administrators can view optional upload settings.',
        correlationId: res.getHeader('X-Correlation-ID'),
      })
    }
    try {
      return res.json(await optionalUploadService.getSettings())
    } catch (error) {
      return errorResponse(req, res, error)
    }
  })

  router.put('/', async (req: AuthenticatedRequest, res) => {
    const correlationId = ensureCorrelationId(req, res)
    if (!isGlobalAdmin(req)) {
      return res.status(403).json({
        error: 'forbidden',
        message: 'Only global administrators can change optional upload settings.',
        correlationId,
      })
    }
    try {
      return res.json(await optionalUploadService.updateSettings(
        req.body,
        req.authorizationContext?.objectId ?? req.user?.userId ?? '',
        correlationId,
      ))
    } catch (error) {
      return errorResponse(req, res, error)
    }
  })

  return router
}
