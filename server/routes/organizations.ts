import { Router } from 'express'
import type { Response } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import {
  ensureCorrelationId,
  mapAuthorizationError,
  sendAuthorizationError,
} from '../services/authorization-errors.js'
import {
  OrganizationAdminError,
  type OrganizationAdminActor,
  type OrganizationAdminService,
} from '../services/organization-admin.js'

function actorFromRequest(req: AuthenticatedRequest, res: Response): OrganizationAdminActor {
  const context = req.authorizationContext
  if (!context) throw new OrganizationAdminError('forbidden', 'Resolved Entra authorization is required.', 403)
  const correlationId = ensureCorrelationId(req, res)
  return {
    tenantId: context.tenantId,
    objectId: context.objectId,
    globalAdmin: context.globalRole === 'admin',
    organizationAdminIds: [...new Set(context.authorizations
      .filter(item => item.role === 'organization_admin' && item.organizationId)
      .map(item => item.organizationId!))],
    correlationId,
  }
}

function sendError(req: AuthenticatedRequest, res: Response, error: unknown): void {
  sendAuthorizationError(req, res, mapAuthorizationError(error, {
    code: 'version_conflict',
    statusCode: 409,
    message: 'The organization operation could not be completed.',
  }))
}

export function createOrganizationsRouter(service: OrganizationAdminService) {
  const router = Router()

  router.get('/', async (req: AuthenticatedRequest, res) => {
    try {
      res.json(await service.listOrganizations(actorFromRequest(req, res)))
    } catch (error) { sendError(req, res, error) }
  })

  router.post('/', async (req: AuthenticatedRequest, res) => {
    try {
      res.status(201).json(await service.createOrganization(actorFromRequest(req, res), req.body))
    } catch (error) { sendError(req, res, error) }
  })

  router.post('/:organizationId/departments', async (req: AuthenticatedRequest, res) => {
    try {
      res.status(201).json(await service.createDepartment(
        actorFromRequest(req, res),
        req.params.organizationId,
        req.body,
      ))
    } catch (error) { sendError(req, res, error) }
  })

  router.patch('/:organizationId/departments/:departmentId', async (req: AuthenticatedRequest, res) => {
    try {
      res.json(await service.updateDepartment(
        actorFromRequest(req, res),
        req.params.organizationId,
        req.params.departmentId,
        req.body,
      ))
    } catch (error) { sendError(req, res, error) }
  })

  router.post('/:organizationId/memberships', async (req: AuthenticatedRequest, res) => {
    try {
      res.json(await service.registerMembership(
        actorFromRequest(req, res),
        req.params.organizationId,
        req.body,
      ))
    } catch (error) { sendError(req, res, error) }
  })

  router.post('/:organizationId/role-assignments', async (req: AuthenticatedRequest, res) => {
    try {
      res.status(201).json(await service.grantRole(
        actorFromRequest(req, res),
        req.params.organizationId,
        req.body,
      ))
    } catch (error) { sendError(req, res, error) }
  })

  router.delete('/:organizationId/role-assignments/:assignmentId', async (req: AuthenticatedRequest, res) => {
    try {
      await service.revokeRole(
        actorFromRequest(req, res),
        req.params.organizationId,
        req.params.assignmentId,
      )
      res.status(204).end()
    } catch (error) { sendError(req, res, error) }
  })

  return router
}