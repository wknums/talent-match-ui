import { createHash } from 'node:crypto'
import { Buffer } from 'node:buffer'
import { randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { userRepo } from '../storage/repos/index.js'
import { auditService } from '../services/audit.js'
import { setCurrentUser, getCurrentUser } from '../session.js'

function sha256(input: string): string {
  return createHash('sha256').update(input).digest('hex')
}

interface EntraClaim {
  typ?: string
  val?: string
}

interface EntraPrincipalPayload {
  claims?: EntraClaim[]
}

interface EntraPrincipal {
  objectId?: string
  preferredUsername?: string
  email?: string
  name?: string
  tenantId?: string
}

function isEntraAuthMode(): boolean {
  return (process.env.APP_AUTH_MODE || 'local').trim().toLowerCase() === 'entra'
}

function getClaimValue(claims: EntraClaim[], ...types: string[]): string | undefined {
  for (const claimType of types) {
    const claim = claims.find(c => c.typ?.toLowerCase() === claimType.toLowerCase())
    const value = claim?.val?.trim()
    if (value) return value
  }
  return undefined
}

function parseEntraPrincipal(encodedPrincipal: string): EntraPrincipal | undefined {
  try {
    const decoded = Buffer.from(encodedPrincipal, 'base64').toString('utf-8')
    const payload = JSON.parse(decoded) as EntraPrincipalPayload
    const claims = payload.claims || []

    return {
      objectId: getClaimValue(
        claims,
        'http://schemas.microsoft.com/identity/claims/objectidentifier',
        'oid',
      ),
      preferredUsername: getClaimValue(
        claims,
        'preferred_username',
        'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn',
        'upn',
      ),
      email: getClaimValue(claims, 'email', 'emails', 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress'),
      name: getClaimValue(claims, 'name', 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name'),
      tenantId: getClaimValue(claims, 'tid'),
    }
  } catch {
    return undefined
  }
}

function normalizeIdentity(value?: string): string | undefined {
  if (!value) return undefined
  const trimmed = value.trim()
  return trimmed ? trimmed.toLowerCase() : undefined
}

export function createAuthRouter() {
  const router = Router()

  // POST /api/auth/login
  router.post('/login', async (req, res, next) => {
    try {
      if (isEntraAuthMode()) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'Local username/password login is disabled when APP_AUTH_MODE=entra. Use /api/auth/entra/login.',
        })
      }

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

  // POST /api/auth/entra/login
  router.post('/entra/login', async (req, res, next) => {
    try {
      if (!isEntraAuthMode()) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'Entra login requires APP_AUTH_MODE=entra.',
        })
      }

      const encodedPrincipal = req.header('x-ms-client-principal')
      if (!encodedPrincipal) {
        return res.status(401).json({
          error: 'Unauthorized',
          message: 'Entra identity header not found. Sign in through App Service Authentication first.',
        })
      }

      const principal = parseEntraPrincipal(encodedPrincipal)
      if (!principal) {
        return res.status(401).json({
          error: 'Unauthorized',
          message: 'Unable to parse Entra identity claims.',
        })
      }

      const identities = [
        normalizeIdentity(principal.preferredUsername),
        normalizeIdentity(principal.email),
      ].filter(Boolean) as string[]

      if (identities.length === 0) {
        return res.status(401).json({
          error: 'Unauthorized',
          message: 'No usable Entra identity claim was provided.',
        })
      }

      const users = await userRepo.getAll()
      const user = users.find(u => {
        const username = normalizeIdentity(u.username)
        const email = normalizeIdentity(u.email)
        return identities.includes(username || '') || identities.includes(email || '')
      })

      if (!user) {
        return res.status(403).json({
          error: 'Forbidden',
          message: 'Your Entra account is authenticated but not authorized for this application role.',
        })
      }

      const now = new Date().toISOString()
      await userRepo.update(user.userId, { lastLogin: now })

      const { passwordHash: _, ...userWithoutPassword } = user
      const sessionUser = { ...userWithoutPassword, lastLogin: now }
      setCurrentUser(sessionUser)

      await auditService.appendEvent(user.username, 'auth.entra-login', 'User', user.userId, {
        preferredUsername: principal.preferredUsername,
        email: principal.email,
        objectId: principal.objectId,
        tenantId: principal.tenantId,
      })

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
      if (isEntraAuthMode()) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'Password management is handled by Entra ID when APP_AUTH_MODE=entra.',
        })
      }

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

  // POST /api/auth/request-password-reset
  router.post('/request-password-reset', async (req, res, next) => {
    try {
      if (isEntraAuthMode()) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'Password reset requests are disabled when APP_AUTH_MODE=entra.',
        })
      }

      const username = String(req.body?.username || '').trim()
      const reason = String(req.body?.reason || '').trim()
      if (!username) {
        return res.status(400).json({ error: 'Validation Error', message: 'Username is required' })
      }

      const user = await userRepo.getByUsername(username)
      if (user) {
        const pending = await userRepo.getResetRequests('pending')
        const alreadyPending = pending.some(r => r.userId === user.userId)
        if (!alreadyPending) {
          await userRepo.createResetRequest({
            requestId: randomUUID(),
            userId: user.userId,
            username: user.username,
            fullName: user.fullName,
            requestedAt: new Date().toISOString(),
            status: 'pending',
          })
          await auditService.appendEvent(user.username, 'auth.password-reset-requested', 'User', user.userId, {
            source: 'login',
            reason: reason || 'Requested from login screen',
          })
        }
      }

      res.json({
        message: 'If the account exists, a password reset request has been submitted for admin review.',
      })
    } catch (err) {
      next(err)
    }
  })

  return router
}
