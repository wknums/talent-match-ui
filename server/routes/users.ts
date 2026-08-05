import { createHash, randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { userRepo } from '../storage/repos/index.js'
import { requireRole } from '../middleware/rbac.js'
import { auditService } from '../services/audit.js'

function sha256(input: string): string {
  return createHash('sha256').update(input).digest('hex')
}

export function createUsersRouter(options: { mode: 'simple' | 'entra' } = { mode: 'simple' }) {
  const router = Router()
  if (options.mode === 'entra') return router

  const audit = auditService

  // GET /api/users - admin: list all users
  router.get('/', requireRole('admin'), async (_req: AuthenticatedRequest, res, next) => {
    try {
      const users = await userRepo.getAll()
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

      const existing = await userRepo.getByUsername(username)
      if (existing) {
        return res.status(409).json({ error: 'Conflict', message: 'Username already exists' })
      }

      const newUser = {
        userId: randomUUID(),
        username,
        role,
        department: department || undefined,
        fullName: fullName || username,
        email: email || undefined,
        createdAt: new Date().toISOString(),
        passwordHash: sha256(password),
      }

      await userRepo.create(newUser)

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

      const existingUser = await userRepo.getById(userId)
      if (!existingUser) {
        return res.status(404).json({ error: 'Not Found', message: 'User not found' })
      }

      // Check email uniqueness across other users
      const allUsers = await userRepo.getAll()
      const emailConflict = allUsers.find(u => u.userId !== userId && u.email?.toLowerCase() === email.toLowerCase())
      if (emailConflict) {
        return res.status(409).json({ error: 'Conflict', message: `Email '${email}' is already in use by another user.` })
      }

      // Self-role-change prevention: silently preserve existing role
      const effectiveRole = req.user?.userId === userId ? existingUser.role : role

      await userRepo.update(userId, { fullName: fullName.trim(), email: email.trim(), role: effectiveRole, department: department ?? existingUser.department })

      await audit.appendEvent(req.user!.username, 'user.updated', 'User', userId, { fullName, email, role: effectiveRole, department })

      const updated = await userRepo.getById(userId)
      if (!updated) return res.status(404).json({ error: 'Not Found' })
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

      const user = await userRepo.getById(userId)
      if (!user) {
        return res.status(404).json({ error: 'Not Found', message: 'User not found' })
      }

      await userRepo.delete(userId)

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

      const user = await userRepo.getById(userId)
      if (!user) {
        return res.status(404).json({ error: 'Not Found', message: 'User not found' })
      }

      await userRepo.update(userId, { passwordHash: sha256(newPassword) })

      await audit.appendEvent(req.user!.username, 'user.password-reset', 'User', userId, { username: user.username })

      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/users/reset-requests - admin: list pending reset requests
  router.get('/reset-requests', requireRole('admin'), async (_req, res, next) => {
    try {
      const requests = await userRepo.getResetRequests('pending')
      res.json(requests)
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

      const request = {
        requestId: randomUUID(),
        userId: user.userId,
        username: user.username,
        fullName: user.fullName,
        requestedAt: new Date().toISOString(),
        status: 'pending' as const,
      }

      await userRepo.createResetRequest(request)
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

      const allRequests = await userRepo.getResetRequests()
      const request = allRequests.find(r => r.requestId === requestId)
      if (!request) {
        return res.status(404).json({ error: 'Not Found', message: 'Request not found' })
      }

      if (action === 'approve') {
        if (!newPassword) {
          return res.status(400).json({ error: 'Validation Error', message: 'newPassword required for approval' })
        }
        await userRepo.update(request.userId, { passwordHash: sha256(newPassword) })
      }

      await userRepo.updateResetRequest(requestId, {
        status: action === 'approve' ? 'completed' : 'rejected',
        resolvedAt: new Date().toISOString(),
        resolvedBy: req.user!.username,
      })

      await audit.appendEvent(req.user!.username, `user.reset-request-${action}`, 'User', request.userId, { requestId })

      res.json({ success: true })
    } catch (err) {
      next(err)
    }
  })

  return router
}
