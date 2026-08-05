import { Router } from 'express'
import type { Response } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import {
  ensureCorrelationId,
  mapAuthorizationError,
  sendAuthorizationError,
} from '../services/authorization-errors.js'
import {
  EntraAccessError,
  type AccessManagementActor,
  type EntraAccessManagementService,
} from '../services/entra-access-management.js'

function actorFromRequest(req: AuthenticatedRequest, res: Response): AccessManagementActor {
  const context = req.authorizationContext
  if (!context) throw new EntraAccessError('forbidden', 'Resolved Entra authorization is required.', 403)
  const correlationId = ensureCorrelationId(req, res)
  return {
    tenantId: context.tenantId,
    objectId: context.objectId,
    globalAdmin: context.globalRole === 'admin',
    organizationAdminIds: context.authorizations
      .filter(item => item.role === 'organization_admin' && item.organizationId)
      .map(item => item.organizationId!),
    correlationId,
  }
}

function sendError(req: AuthenticatedRequest, res: Response, error: unknown): void {
  sendAuthorizationError(req, res, mapAuthorizationError(error, {
    code: 'invalid_scope',
    statusCode: 500,
    message: 'The access-management operation could not be completed.',
  }))
}

export function createAccessManagementRouter(service: EntraAccessManagementService) {
  const router = Router()

  router.get('/users', async (req: AuthenticatedRequest, res) => {
    try {
      const limit = req.query.limit === undefined ? 25 : Number(req.query.limit)
      const page = await service.list(actorFromRequest(req, res), {
        search: typeof req.query.search === 'string' ? req.query.search : undefined,
        organizationId: typeof req.query.organizationId === 'string' ? req.query.organizationId : undefined,
        status: typeof req.query.status === 'string'
          ? req.query.status as 'pending' | 'active' | 'disabled'
          : undefined,
        cursor: typeof req.query.cursor === 'string' ? req.query.cursor : undefined,
        limit,
      })
      res.json(page)
    } catch (error) { sendError(req, res, error) }
  })

  router.get('/users/:objectId', async (req: AuthenticatedRequest, res) => {
    try {
      res.json(await service.get(actorFromRequest(req, res), req.params.objectId))
    } catch (error) { sendError(req, res, error) }
  })

  router.patch('/users/:objectId', async (req: AuthenticatedRequest, res) => {
    try {
      res.json(await service.updateUser(actorFromRequest(req, res), req.params.objectId, req.body))
    } catch (error) { sendError(req, res, error) }
  })

  router.put('/users/:objectId/organizations/:organizationId', async (req: AuthenticatedRequest, res) => {
    try {
      res.json(await service.putOrganizationAccess(
        actorFromRequest(req, res),
        req.params.objectId,
        req.params.organizationId,
        req.body,
      ))
    } catch (error) { sendError(req, res, error) }
  })

  router.delete('/users/:objectId/organizations/:organizationId/role-assignments/:assignmentId', async (req: AuthenticatedRequest, res) => {
    try {
      res.json(await service.revokeRole(
        actorFromRequest(req, res),
        req.params.objectId,
        req.params.organizationId,
        req.params.assignmentId,
        Number(req.query.expectedVersion),
      ))
    } catch (error) { sendError(req, res, error) }
  })

  return router
}