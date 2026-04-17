import type { Request, Response, NextFunction } from 'express'
import { getCurrentUser } from '../session.js'

export interface User {
  userId: string
  username: string
  role: 'admin' | 'recruiter' | 'business_panel'
  department?: string
  fullName: string
  email?: string
  createdAt: string
  lastLogin?: string
  passwordResetRequired?: boolean
}

export interface AuthenticatedRequest extends Request {
  user?: User
}

export function createAuthMiddleware() {
  return async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
    try {
      const user = getCurrentUser()
      if (!user) {
        return res.status(401).json({
          error: 'Unauthorized',
          message: 'Authentication required',
        })
      }
      req.user = user
      next()
    } catch (err) {
      next(err)
    }
  }
}
