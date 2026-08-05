import { once } from 'node:events'
import type { AddressInfo } from 'node:net'
import express from 'express'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { EntraAccessUser, PutOrganizationAccessRequest } from '../../src/types/index.js'
import {
  EntraAccessError,
  EntraAccessManagementService,
  type AccessManagementActor,
} from '../../server/services/entra-access-management.js'
import type { EntraAccessManagementRepository } from '../../server/storage/repos/access-management-repo.js'
import { createAccessManagementRouter } from '../../server/routes/access-management.js'

const tenantId = '11111111-1111-4111-8111-111111111111'
const actorObjectId = '22222222-2222-4222-8222-222222222222'
const targetObjectId = '33333333-3333-4333-8333-333333333333'
const organizationId = '44444444-4444-4444-8444-444444444444'
const otherOrganizationId = '55555555-5555-4555-8555-555555555555'
const departmentId = '66666666-6666-4666-8666-666666666666'

const target: EntraAccessUser = {
  objectId: targetObjectId,
  username: 'target@example.com',
  fullName: 'Target User',
  email: 'target@example.com',
  isActive: true,
  authorizationVersion: 0,
  organizations: [],
}

function actor(role: 'admin' | 'organization_admin' = 'admin'): AccessManagementActor {
  return {
    tenantId,
    objectId: actorObjectId,
    globalAdmin: role === 'admin',
    organizationAdminIds: role === 'organization_admin' ? [organizationId] : [],
    correlationId: '77777777-7777-4777-8777-777777777777',
  }
}

function request(role: 'organization_admin' | 'recruiter' | 'business_panel' = 'recruiter'): PutOrganizationAccessRequest {
  return {
    expectedVersion: 0,
    profile: {
      username: 'target@example.com',
      fullName: 'Target User',
      email: 'target@example.com',
    },
    membership: {
      status: 'active',
      departmentIds: [departmentId],
      defaultDepartmentId: departmentId,
    },
    roleAssignments: [{ role, departmentId: role === 'recruiter' ? departmentId : null }],
  }
}

function repository(): EntraAccessManagementRepository {
  return {
    list: vi.fn(async () => ({ items: [target], nextCursor: null })),
    get: vi.fn(async () => target),
    updateUser: vi.fn(async () => ({ ...target, authorizationVersion: 1 })),
    putOrganizationAccess: vi.fn(async () => ({
      ...target,
      authorizationVersion: 1,
      organizations: [{
        organizationId,
        status: 'active',
        departmentIds: [departmentId],
        defaultDepartmentId: departmentId,
        roleAssignments: [],
      }],
    })),
    revokeRole: vi.fn(async () => ({ ...target, authorizationVersion: 1 })),
  }
}

const closeCallbacks: Array<() => Promise<void>> = []

afterEach(async () => {
  await Promise.all(closeCallbacks.splice(0).map(close => close()))
})

async function createServer(repo = repository()) {
  const app = express()
  app.use(express.json())
  app.use((req, _res, next) => {
    ;(req as any).authorizationContext = {
      tenantId,
      objectId: actorObjectId,
      globalRole: 'admin',
      authorizations: [],
    }
    next()
  })
  app.use('/api/access-management', createAccessManagementRouter(new EntraAccessManagementService(repo)))
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  const close = () => new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()))
  closeCallbacks.push(close)
  return {
    repo,
    request: (path: string, init: RequestInit = {}) => fetch(`http://127.0.0.1:${port}${path}`, init),
  }
}

describe('Stack A Entra access-management authority and validation', () => {
  it('converges a pending profile into an explicit-default organization aggregate', async () => {
    const repo = repository()
    const service = new EntraAccessManagementService(repo)

    const result = await service.putOrganizationAccess(actor(), targetObjectId, organizationId, request())

    expect(result.authorizationVersion).toBe(1)
    expect(repo.putOrganizationAccess).toHaveBeenCalledWith(
      actor(), targetObjectId, organizationId, request(),
    )
  })

  it('rejects an active membership whose default is not one of its departments', async () => {
    const service = new EntraAccessManagementService(repository())
    const invalid = request()
    invalid.membership.defaultDepartmentId = otherOrganizationId

    await expect(service.putOrganizationAccess(actor(), targetObjectId, organizationId, invalid))
      .rejects.toMatchObject({ code: 'invalid_scope', statusCode: 400 })
  })

  it('limits Organization Admin to assigned organizations', async () => {
    const service = new EntraAccessManagementService(repository())

    await expect(service.putOrganizationAccess(
      actor('organization_admin'), targetObjectId, otherOrganizationId, request(),
    )).rejects.toMatchObject({ code: 'forbidden', statusCode: 403 })
  })

  it('prevents Organization Admin from granting Organization Admin', async () => {
    const service = new EntraAccessManagementService(repository())

    await expect(service.putOrganizationAccess(
      actor('organization_admin'), targetObjectId, organizationId, request('organization_admin'),
    )).rejects.toMatchObject({ code: 'forbidden', statusCode: 403 })
  })

  it('requires application Admin for global disable or profile mutation', async () => {
    const service = new EntraAccessManagementService(repository())

    await expect(service.updateUser(actor('organization_admin'), targetObjectId, {
      expectedVersion: 0,
      isActive: false,
    })).rejects.toBeInstanceOf(EntraAccessError)
  })

  it('validates pagination and delegates actor-filtered list/detail reads', async () => {
    const repo = repository()
    const service = new EntraAccessManagementService(repo)

    await service.list(actor('organization_admin'), { search: 'target', limit: 25 })
    await service.get(actor('organization_admin'), targetObjectId)

    expect(repo.list).toHaveBeenCalledWith(actor('organization_admin'), { search: 'target', limit: 25 })
    expect(repo.get).toHaveBeenCalledWith(actor('organization_admin'), targetObjectId)
    await expect(service.list(actor(), { limit: 101 })).rejects.toMatchObject({ code: 'invalid_scope' })
  })

  it('permits only targeted delegated-role revocation inside actor scope', async () => {
    const repo = repository()
    const service = new EntraAccessManagementService(repo)

    await service.revokeRole(actor('organization_admin'), targetObjectId, organizationId, 'assignment-1', 3)

    expect(repo.revokeRole).toHaveBeenCalledWith(
      actor('organization_admin'), targetObjectId, organizationId, 'assignment-1', 3,
    )
    await expect(service.revokeRole(
      actor('organization_admin'), targetObjectId, otherOrganizationId, 'assignment-1', 3,
    )).rejects.toMatchObject({ code: 'forbidden' })
  })
})

describe('Stack A Entra access-management HTTP contract', () => {
  it('maps list, detail, patch, desired organization access, and targeted revoke', async () => {
    const server = await createServer()
    const json = { 'Content-Type': 'application/json' }

    const responses = await Promise.all([
      server.request('/api/access-management/users?search=target&limit=25'),
      server.request(`/api/access-management/users/${targetObjectId}`),
      server.request(`/api/access-management/users/${targetObjectId}`, {
        method: 'PATCH', headers: json, body: JSON.stringify({ expectedVersion: 0, isActive: false }),
      }),
      server.request(`/api/access-management/users/${targetObjectId}/organizations/${organizationId}`, {
        method: 'PUT', headers: json, body: JSON.stringify(request()),
      }),
      server.request(`/api/access-management/users/${targetObjectId}/organizations/${organizationId}/role-assignments/assignment-1?expectedVersion=0`, {
        method: 'DELETE',
      }),
    ])

    expect(responses.map(response => response.status)).toEqual([200, 200, 200, 200, 200])
  })

  it('returns a safe correlated version conflict', async () => {
    const repo = repository()
    vi.mocked(repo.updateUser).mockRejectedValue(
      new EntraAccessError('version_conflict', 'Authorization state changed. Refresh and retry.', 409),
    )
    const server = await createServer(repo)

    const response = await server.request(`/api/access-management/users/${targetObjectId}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expectedVersion: 0, isActive: false }),
    })
    const body = await response.json() as Record<string, string>

    expect(response.status).toBe(409)
    expect(body).toMatchObject({ error: 'version_conflict', message: expect.any(String) })
    expect(body.correlationId).toMatch(/^[0-9a-f-]{36}$/i)
    expect(response.headers.get('x-correlation-id')).toBe(body.correlationId)
  })
})
