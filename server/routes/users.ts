import { createHash, randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { AUTH_USERS, AUTH_RESET_REQUESTS } from '../storage/kv-keys.js'
import { getArray, setArray, pushToArray, removeFromArray } from '../storage/kv-helpers.js'
import { requireRole } from '../middleware/rbac.js'
import { createAuditService } from '../services/audit.js'
import type { PasswordResetRequest } from '../../src/types/index.js'

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

export function createUsersRouter(storage: StorageProvider) {
  const router = Router()
  const audit = createAuditService(storage)

  // GET /api/users - admin: list all users
  router.get('/', requireRole('admin'), async (_req: AuthenticatedRequest, res, next) => {
    try {
      const users = await getArray<StoredUser>(storage, AUTH_USERS)
      const safeUsers = users.map(({ passwordHash, ...u }) => u)
      res.json(safeUsers)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/users - admin: create user
  router.post('/', requireRole('admin'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const { username, role, department, password, fullName, email } = req.body
      if (!username || !role || !password) {
        return res.status(400).json({ error: 'Validation Error', message: 'username, role, and password are required' })
      }

      const users = await getArray<StoredUser>(storage, AUTH_USERS)
      if (users.find(u => u.username === username)) {
        return res.status(409).json({ error: 'Conflict', message: 'Username already exists' })
      }

      const newUser: StoredUser = {
        userId: randomUUID(),
        username,
        role,
        department: department || undefined,
        fullName: fullName || username,
        email: email || undefined,
        createdAt: new Date().toISOString(),
        passwordHash: sha256(password),
      }

      users.push(newUser)
      await setArray(storage, AUTH_USERS, users)

      await audit.appendEvent(req.user!.username, 'user.created', 'User', newUser.userId, { username, role, department })

      const { passwordHash, ...safeUser } = newUser
      res.status(201).json(safeUser)
    } catch (err) {
      next(err)
    }
  })

  // PUT /api/users/:userId - admin: update user
  router.put('/:userId', requireRole('admin'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const { userId } = req.params
      const { fullName, email, role, department } = req.body

      // Validate required fields
      if (!fullName || typeof fullName !== 'string' || !fullName.trim()) {
        return res.status(400).json({ error: 'Validation Error', message: 'Full name is required' })
      }
      if (!email || typeof email !== 'string' || !email.trim()) {
        return res.status(400).json({ error: 'Validation Error', message: 'Email is required' })
      }
      const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
      if (!emailRegex.test(email)) {
        return res.status(400).json({ error: 'Validation Error', message: 'Email must be a valid email address' })
      }
      const validRoles = ['admin', 'recruiter', 'business_panel']
      if (!role || !validRoles.includes(role)) {
        return res.status(400).json({ error: 'Validation Error', message: "Role must be 'admin', 'recruiter', or 'business_panel'" })
      }

      const users = await getArray<StoredUser>(storage, AUTH_USERS)
      const userIndex = users.findIndex(u => u.userId === userId)
      if (userIndex === -1) {
        return res.status(404).json({ error: 'Not Found', message: 'User not found' })
      }

      // Check email uniqueness across other users
      const emailConflict = users.find(u => u.userId !== userId && u.email?.toLowerCase() === email.toLowerCase())
      if (emailConflict) {
        return res.status(409).json({ error: 'Conflict', message: `Email '${email}' is already in use by another user.` })
      }

      // Self-role-change prevention: silently preserve existing role
      const effectiveRole = req.user?.userId === userId ? users[userIndex].role : role

      const updatedUsers = users.map(u =>
        u.userId === userId
          ? { ...u, fullName: fullName.trim(), email: email.trim(), role: effectiveRole, department: department ?? u.department }
          : u
      )
      await setArray(storage, AUTH_USERS, updatedUsers)

      await audit.appendEvent(req.user!.username, 'user.updated', 'User', userId, { fullName, email, role: effectiveRole, department })

      const updated = updatedUsers.find(u => u.userId === userId)!
      const { passwordHash, ...safeUser } = updated
      res.json(safeUser)
    } catch (err) {
      next(err)
    }
  })

  // DELETE /api/users/:userId - admin: delete user
  router.delete('/:userId', requireRole('admin'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const { userId } = req.params
      if (userId === req.user?.userId) {
        return res.status(400).json({ error: 'Validation Error', message: 'Cannot delete your own account' })
      }

      const users = await getArray<StoredUser>(storage, AUTH_USERS)
      const user = users.find(u => u.userId === userId)
      if (!user) {
        return res.status(404).json({ error: 'Not Found', message: 'User not found' })
      }

      const remaining = users.filter(u => u.userId !== userId)
      await setArray(storage, AUTH_USERS, remaining)

      await audit.appendEvent(req.user!.username, 'user.deleted', 'User', userId, { username: user.username })

      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/users/:userId/reset-password - admin: set new password
  router.post('/:userId/reset-password', requireRole('admin'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const { userId } = req.params
      const { newPassword } = req.body
      if (!newPassword) {
        return res.status(400).json({ error: 'Validation Error', message: 'newPassword is required' })
      }

      const users = await getArray<StoredUser>(storage, AUTH_USERS)
      const user = users.find(u => u.userId === userId)
      if (!user) {
        return res.status(404).json({ error: 'Not Found', message: 'User not found' })
      }

      const updatedUsers = users.map(u =>
        u.userId === userId ? { ...u, passwordHash: sha256(newPassword) } : u
      )
      await setArray(storage, AUTH_USERS, updatedUsers)

      await audit.appendEvent(req.user!.username, 'user.password-reset', 'User', userId, { username: user.username })

      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/users/reset-requests - admin: list pending reset requests
  router.get('/reset-requests', requireRole('admin'), async (_req, res, next) => {
    try {
      const requests = await getArray<PasswordResetRequest>(storage, AUTH_RESET_REQUESTS)
      res.json(requests.filter(r => r.status === 'pending'))
    } catch (err) {
      next(err)
    }
  })

  // POST /api/users/reset-requests - recruiter: submit reset request
  router.post('/reset-requests', async (req: AuthenticatedRequest, res, next) => {
    try {
      const user = req.user
      if (!user) {
        return res.status(401).json({ error: 'Unauthorized' })
      }

      const request: PasswordResetRequest = {
        requestId: randomUUID(),
        userId: user.userId,
        username: user.username,
        fullName: user.fullName,
        requestedAt: new Date().toISOString(),
        status: 'pending',
      }

      await pushToArray(storage, AUTH_RESET_REQUESTS, request)
      res.status(201).json(request)
    } catch (err) {
      next(err)
    }
  })

  // PUT /api/users/reset-requests/:requestId - admin: approve/reject
  router.put('/reset-requests/:requestId', requireRole('admin'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const { requestId } = req.params
      const { action, newPassword } = req.body // action: 'approve' | 'reject'

      const requests = await getArray<PasswordResetRequest>(storage, AUTH_RESET_REQUESTS)
      const request = requests.find(r => r.requestId === requestId)
      if (!request) {
        return res.status(404).json({ error: 'Not Found', message: 'Request not found' })
      }

      if (action === 'approve') {
        if (!newPassword) {
          return res.status(400).json({ error: 'Validation Error', message: 'newPassword required for approval' })
        }
        // Update user password
        const users = await getArray<StoredUser>(storage, AUTH_USERS)
        const updatedUsers = users.map(u =>
          u.userId === request.userId ? { ...u, passwordHash: sha256(newPassword) } : u
        )
        await setArray(storage, AUTH_USERS, updatedUsers)
      }

      const updatedRequests = requests.map(r =>
        r.requestId === requestId
          ? { ...r, status: action === 'approve' ? 'completed' as const : 'rejected' as const, resolvedAt: new Date().toISOString(), resolvedBy: req.user!.username }
          : r
      )
      await setArray(storage, AUTH_RESET_REQUESTS, updatedRequests)

      await audit.appendEvent(req.user!.username, `user.reset-request-${action}`, 'User', request.userId, { requestId })

      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  return router
}
