import type { Request, Response, NextFunction } from 'express'

export interface AppError extends Error {
  statusCode?: number
  details?: unknown
}

export function errorHandler(err: AppError, _req: Request, res: Response, _next: NextFunction) {
  const statusCode = err.statusCode || 500
  const message = err.message || 'Internal server error'

  console.error(`[ERROR] ${statusCode} - ${message}`, err.stack)

  res.status(statusCode).json({
    error: statusCode >= 500 ? 'Internal Server Error' : 'Request Error',
    message,
    statusCode,
  })
}
