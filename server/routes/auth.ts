import { createHash, randomUUID } from 'node:crypto'
import { Router, type RequestHandler } from 'express'
import type { AuthenticatedRequest, AuthorizationAuditWriter } from '../middleware/auth.js'
import { userRepo } from '../storage/repos/index.js'
import { auditService } from '../services/audit.js'
import type { AuthorizationRepositories } from '../services/authorization.js'
import { setCurrentUser, getCurrentUser } from '../session.js'

function sha256(input: string): string {
  return createHash('sha256').update(input).digest('hex')
}

export interface AuthRouterOptions {
  mode?: 'simple' | 'entra'
  authMiddleware?: RequestHandler
  audit?: AuthorizationAuditWriter
  users?: Pick<AuthorizationRepositories['users'], 'update'>
}

export function createAuthRouter(options: AuthRouterOptions = {}) {
  const router = Router()
  const mode = options.mode ?? (process.env.APP_AUTH_MODE === 'entra' ? 'entra' : 'simple')
  const audit = options.audit ?? auditService
  const profileUsers = options.users ?? userRepo

  router.use((_req, res, next) => {
    if (!res.getHeader('X-Correlation-ID')) {
      res.setHeader('X-Correlation-ID', randomUUID())
    }
    next()
  })

  if (mode === 'entra') {
    if (!options.authMiddleware) throw new Error('Entra auth middleware is required.')

    router.get('/me', options.authMiddleware, async (req: AuthenticatedRequest, res, next) => {
      try {
        const context = req.authorizationContext!
        await profileUsers.update(context.userId, { lastLogin: new Date().toISOString() })
        await audit.appendAuthorizationEvent(context.objectId, 'auth.login.succeeded', context.userId, {
          subjectObjectId: context.objectId,
          tenantId: context.tenantId,
          result: 'succeeded',
        }, String(res.getHeader('X-Correlation-ID')))
        res.json(context)
      } catch (err) {
        next(err)
      }
    })

    router.post('/logout', options.authMiddleware, async (req: AuthenticatedRequest, res, next) => {
      try {
        const context = req.authorizationContext!
        await audit.appendAuthorizationEvent(context.objectId, 'auth.logout', context.userId, {
          subjectObjectId: context.objectId,
          tenantId: context.tenantId,
          result: 'succeeded',
        }, String(res.getHeader('X-Correlation-ID')))
        res.status(204).end()
      } catch (err) {
        next(err)
      }
    })

    return router
  }

  // POST /api/auth/login
  router.post('/login', async (req, res, next) => {
    try {
      const { username, password } = req.body
      if (!username || !password) {
        return res.status(400).json({ error: 'Validation Error', message: 'Username and password are required' })
      }

      const user = await userRepo.getByUsername(username)
      const passwordHash = sha256(password)
      if (!user || user.passwordHash !== passwordHash) {
        return res.status(401).json({ error: 'Unauthorized', message: 'Invalid credentials' })
      }

      // Update last login
      const now = new Date().toISOString()
      await userRepo.update(user.userId, { lastLogin: now })

      const { passwordHash: _, ...userWithoutPassword } = user
      const sessionUser = { ...userWithoutPassword, lastLogin: now }
      setCurrentUser(sessionUser)

      await auditService.appendEvent(user.username, 'auth.login', 'User', user.userId, { username })

      res.json(sessionUser)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/auth/logout
  router.post('/logout', async (_req, res, next) => {
    try {
      setCurrentUser(null)
      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/auth/me
  router.get('/me', async (_req, res, next) => {
    try {
      const user = getCurrentUser()
      if (!user) {
        return res.json(null)
      }
      res.json(user)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/auth/change-password
  router.post('/change-password', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { currentPassword, newPassword } = req.body
      if (!currentPassword || !newPassword) {
        return res.status(400).json({ error: 'Validation Error', message: 'Current and new password are required' })
      }

      if (!req.user) {
        return res.status(401).json({ error: 'Unauthorized' })
      }

      const user = await userRepo.getById(req.user.userId)
      if (!user || user.passwordHash !== sha256(currentPassword)) {
        return res.status(400).json({ error: 'Validation Error', message: 'Current password is incorrect' })
      }

      await userRepo.update(user.userId, { passwordHash: sha256(newPassword), passwordResetRequired: false })

      await auditService.appendEvent(user.username, 'auth.password-changed', 'User', user.userId, {})

      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  return router
}
