import { randomUUID } from 'node:crypto'
import type { Request, Response, NextFunction } from 'express'
import type { AuthErrorCode, UserRole } from '../../src/types/index.js'
import { getCurrentUser } from '../session.js'
import type { AuthorizationAuditAction, AuthorizationAuditDetails } from '../services/audit.js'
import { auditService } from '../services/audit.js'
import {
  AuthorizationError,
  getAuthorizationErrorMessage,
  type ServerAuthorizationContext,
} from '../services/authorization.js'
import { EntraTokenError, type NormalizedEntraClaims } from '../services/entra-token.js'

export interface User {
  userId: string
  username: string
  role: 'admin' | 'organization_admin' | 'recruiter' | 'business_panel'
  department?: string
  fullName: string
  email?: string
  createdAt: string
  lastLogin?: string
  passwordResetRequired?: boolean
}

export interface AuthenticatedRequest extends Request {
  user?: User
  entraClaims?: NormalizedEntraClaims
  authorizationContext?: ServerAuthorizationContext
}

interface TokenValidator {
  validate(token: string): Promise<NormalizedEntraClaims>
}

interface AuthorizationResolver {
  resolve(claims: NormalizedEntraClaims): Promise<ServerAuthorizationContext>
}

export interface AuthorizationAuditWriter {
  appendAuthorizationEvent(
    actor: string,
    action: AuthorizationAuditAction,
    subjectId: string,
    details: AuthorizationAuditDetails,
    correlationId?: string,
  ): Promise<unknown>
}

export interface AuthMiddlewareOptions {
  mode?: 'simple' | 'entra'
  tokenValidator?: TokenValidator
  authorizationResolver?: AuthorizationResolver
  audit?: AuthorizationAuditWriter
}

const roleRank: Record<UserRole, number> = {
  admin: 4,
  organization_admin: 3,
  recruiter: 2,
  business_panel: 1,
}

function toCompatibilityUser(context: ServerAuthorizationContext): User {
  const primary = [...context.authorizations].sort((left, right) => roleRank[right.role] - roleRank[left.role])[0]
  const department = primary?.departmentId
    ? context.memberships.flatMap(item => item.departments).find(item => item.departmentId === primary.departmentId)?.departmentName
    : undefined
  return {
    userId: context.userId,
    username: context.username,
    role: primary?.role ?? 'business_panel',
    department,
    fullName: context.fullName,
    email: context.email ?? undefined,
    createdAt: context.tokenIssuedAt,
  }
}

function denialAction(code: AuthErrorCode): AuthorizationAuditAction {
  if (code === 'token_stale') return 'auth.token.stale'
  if (code === 'role_missing') return 'auth.role.missing'
  if (code === 'role_conflict') return 'auth.role.conflict'
  if (code === 'scope_unmapped' || code === 'membership_missing' || code === 'invalid_job_scope') {
    return 'auth.scope.unmapped'
  }
  return 'auth.login.denied'
}

function correlationId(req: Request, res: Response): string {
  const existing = res.getHeader('X-Correlation-ID')
  if (typeof existing === 'string') return existing
  const value = req.get('X-Correlation-ID') ?? randomUUID()
  res.setHeader('X-Correlation-ID', value)
  return value
}

export function createAuthMiddleware(options: AuthMiddlewareOptions = {}) {
  const mode = options.mode ?? (process.env.APP_AUTH_MODE === 'entra' ? 'entra' : 'simple')
  const audit = options.audit ?? auditService

  return async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
    if (mode === 'simple') {
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
      return
    }

    const requestCorrelationId = correlationId(req, res)
    let claims: NormalizedEntraClaims | undefined
    try {
      const authorization = req.get('Authorization')
      if (!authorization) throw new EntraTokenError('auth_required', '')
      const match = /^Bearer ([^\s]+)$/i.exec(authorization)
      if (!match) throw new EntraTokenError('invalid_token', '')
      if (!options.tokenValidator || !options.authorizationResolver) {
        throw new Error('Entra authentication dependencies are not configured.')
      }

      claims = await options.tokenValidator.validate(match[1])
      const context = await options.authorizationResolver.resolve(claims)
      req.entraClaims = claims
      req.authorizationContext = context
      req.user = toCompatibilityUser(context)
      next()
    } catch (err) {
      if (!(err instanceof EntraTokenError) && !(err instanceof AuthorizationError)) {
        const code: AuthErrorCode = 'invalid_token'
        await audit.appendAuthorizationEvent('unknown', denialAction(code), 'unknown', {
          result: 'denied',
          reasonCode: code,
        }, requestCorrelationId)
        return res.status(401).json({
          error: code,
          message: getAuthorizationErrorMessage(code),
          correlationId: requestCorrelationId,
        })
      }

      const code = err.code
      const actor = claims?.objectId ?? 'unknown'
      await audit.appendAuthorizationEvent(actor, denialAction(code), actor, {
        subjectObjectId: claims?.objectId,
        tenantId: claims?.tenantId,
        result: 'denied',
        reasonCode: code,
      }, requestCorrelationId)
      return res.status(err.statusCode).json({
        error: code,
        message: getAuthorizationErrorMessage(code),
        correlationId: requestCorrelationId,
      })
    }
  }
}
