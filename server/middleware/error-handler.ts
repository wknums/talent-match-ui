import type { Request, Response, NextFunction } from 'express'
import { ensureCorrelationId } from '../services/authorization-errors.js'

export interface AppError extends Error {
  statusCode?: number
  details?: unknown
}

export function errorHandler(err: AppError, _req: Request, res: Response, _next: NextFunction) {
  const statusCode = err.statusCode || 500
  const publicMessage = statusCode >= 500 ? 'Internal server error' : (err.message || 'Request error')
  const correlationId = ensureCorrelationId(_req, res)

  console.error(`[ERROR] ${statusCode} - ${err.message || 'Internal server error'} [${correlationId}]`, err.stack)

  res.status(statusCode).json({
    error: statusCode >= 500 ? 'Internal Server Error' : 'Request Error',
    message: publicMessage,
    statusCode,
    correlationId,
  })
}
