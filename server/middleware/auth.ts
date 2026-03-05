import type { Request, Response, NextFunction } from 'express'
import type { StorageProvider } from '../storage/types.js'
import { AUTH_CURRENT_USER } from '../storage/kv-keys.js'

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

export function createAuthMiddleware(storage: StorageProvider) {
  return async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
    try {
      const user = await storage.get<User>(AUTH_CURRENT_USER)
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
