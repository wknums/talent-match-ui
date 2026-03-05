import { createHash, randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { AUTH_USERS, AUTH_CURRENT_USER } from '../storage/kv-keys.js'
import { getArray, setArray } from '../storage/kv-helpers.js'
import { createAuditService } from '../services/audit.js'

interface StoredUser {
  userId: string
  username: string
  role: 'admin' | 'recruiter' | 'business_panel'
  department?: string
  fullName: string
  email?: string
  createdAt: string
  lastLogin?: string
  passwordHash: string
  passwordResetRequired?: boolean
}

function sha256(input: string): string {
  return createHash('sha256').update(input).digest('hex')
}

export function createAuthRouter(storage: StorageProvider) {
  const router = Router()
  const audit = createAuditService(storage)

  // POST /api/auth/login
  router.post('/login', async (req, res, next) => {
    try {
      const { username, password } = req.body
      if (!username || !password) {
        return res.status(400).json({ error: 'Validation Error', message: 'Username and password are required' })
      }

      const users = await getArray<StoredUser>(storage, AUTH_USERS)
      const passwordHash = sha256(password)
      const user = users.find(u => u.username === username && u.passwordHash === passwordHash)

      if (!user) {
        return res.status(401).json({ error: 'Unauthorized', message: 'Invalid credentials' })
      }

      // Update last login
      const updatedUsers = users.map(u =>
        u.userId === user.userId ? { ...u, lastLogin: new Date().toISOString() } : u
      )
      await setArray(storage, AUTH_USERS, updatedUsers)

      const { passwordHash: _, ...userWithoutPassword } = user
      const sessionUser = { ...userWithoutPassword, lastLogin: new Date().toISOString() }
      await storage.set(AUTH_CURRENT_USER, sessionUser)

      await audit.appendEvent(user.username, 'auth.login', 'User', user.userId, { username })

      res.json(sessionUser)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/auth/logout
  router.post('/logout', async (req, res, next) => {
    try {
      await storage.delete(AUTH_CURRENT_USER)
      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/auth/me
  router.get('/me', async (req, res, next) => {
    try {
      const user = await storage.get(AUTH_CURRENT_USER)
      if (!user) {
        return res.status(401).json({ error: 'Unauthorized', message: 'No active session' })
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

      const users = await getArray<StoredUser>(storage, AUTH_USERS)
      const currentHash = sha256(currentPassword)
      const user = users.find(u => u.userId === req.user?.userId && u.passwordHash === currentHash)

      if (!user) {
        return res.status(400).json({ error: 'Validation Error', message: 'Current password is incorrect' })
      }

      const updatedUsers = users.map(u =>
        u.userId === user.userId ? { ...u, passwordHash: sha256(newPassword), passwordResetRequired: false } : u
      )
      await setArray(storage, AUTH_USERS, updatedUsers)

      await audit.appendEvent(user.username, 'auth.password-changed', 'User', user.userId, {})

      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  return router
}
