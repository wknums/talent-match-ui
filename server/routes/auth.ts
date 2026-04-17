import { createHash } from 'node:crypto'
import { Router } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { userRepo } from '../storage/repos/index.js'
import { auditService } from '../services/audit.js'
import { setCurrentUser, getCurrentUser } from '../session.js'

function sha256(input: string): string {
  return createHash('sha256').update(input).digest('hex')
}

export function createAuthRouter() {
  const router = Router()

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
