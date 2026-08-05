import { once } from 'node:events'
import type { AddressInfo } from 'node:net'
import express from 'express'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createOrganizationsRouter } from '../../server/routes/organizations.js'
import {
  OrganizationAdminService,
  type OrganizationAdminActor,
  type OrganizationAdminRepository,
} from '../../server/services/organization-admin.js'

const tenantId = '11111111-1111-4111-8111-111111111111'
const actorObjectId = '22222222-2222-4222-8222-222222222222'
const targetObjectId = '33333333-3333-4333-8333-333333333333'
const organizationId = '44444444-4444-4444-8444-444444444444'
const otherOrganizationId = '55555555-5555-4555-8555-555555555555'
const engineeringDepartmentId = '66666666-6666-4666-8666-666666666666'
const operationsDepartmentId = '77777777-7777-4777-8777-777777777777'
const assignmentId = '88888888-8888-4888-8888-888888888888'
const unrelatedAssignmentId = '99999999-9999-4999-8999-999999999999'
const correlationId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'

const engineeringDepartment = {
  id: engineeringDepartmentId,
  organizationId,
  name: 'Engineering',
  status: 'active' as const,
}

const operationsDepartment = {
  id: operationsDepartmentId,
  organizationId,
  name: 'Operations',
  status: 'active' as const,
}

const membership = {
  userObjectId: targetObjectId,
  organizationId,
  departmentIds: [engineeringDepartmentId, operationsDepartmentId],
  defaultDepartmentId: operationsDepartmentId,
}

const recruiterAssignment = {
  id: assignmentId,
  userObjectId: targetObjectId,
  role: 'recruiter' as const,
  organizationId,
  departmentId: engineeringDepartmentId,
  source: 'delegated' as const,
  status: 'active' as const,
}

function actor(role: 'admin' | 'organization_admin' = 'organization_admin'): OrganizationAdminActor {
  return {
    tenantId,
    objectId: actorObjectId,
    globalAdmin: role === 'admin',
    organizationAdminIds: role === 'organization_admin' ? [organizationId] : [],
    correlationId,
  }
}

function repository(): OrganizationAdminRepository {
  return {
    createOrganization: vi.fn(async () => ({
      id: organizationId,
      name: 'Contoso',
      status: 'active' as const,
      departments: [engineeringDepartment],
    })),
    createDepartment: vi.fn(async () => operationsDepartment),
    updateDepartment: vi.fn(async (_actor, _organizationId, _departmentId, request) => ({
      ...engineeringDepartment,
      name: request.name ?? engineeringDepartment.name,
      status: request.status ?? engineeringDepartment.status,
    })),
    registerMembership: vi.fn(async () => membership),
    grantRole: vi.fn(async () => recruiterAssignment),
    revokeRole: vi.fn(async () => undefined),
  }
}

const closeCallbacks: Array<() => Promise<void>> = []

afterEach(async () => {
  await Promise.all(closeCallbacks.splice(0).map(close => close()))
})

async function createServer(
  role: 'admin' | 'organization_admin' = 'organization_admin',
  repo = repository(),
) {
  const app = express()
  app.use(express.json())
  app.use((req, _res, next) => {
    ;(req as any).authorizationContext = {
      tenantId,
      objectId: actorObjectId,
      globalRole: role === 'admin' ? 'admin' : null,
      authorizations: role === 'organization_admin'
        ? [{ role: 'organization_admin', organizationId, departmentId: null, source: 'delegated' }]
        : [],
    }
    next()
  })
  app.use('/api/organizations', createOrganizationsRouter(new OrganizationAdminService(repo)))
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  const close = () => new Promise<void>((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve())
  })
  closeCallbacks.push(close)
  return {
    repo,
    request: (path: string, init: RequestInit = {}) => fetch(`http://127.0.0.1:${port}${path}`, {
      ...init,
      headers: { 'X-Correlation-ID': correlationId, ...init.headers },
    }),
  }
}

describe('Stack A Organization Admin service contract', () => {
  it('creates, renames, and retires departments only in the administered organization', async () => {
    const repo = repository()
    const service = new OrganizationAdminService(repo)

    const created = await service.createDepartment(actor(), organizationId, { name: 'Operations' })
    const renamed = await service.updateDepartment(
      actor(), organizationId, engineeringDepartmentId, { name: 'Product Engineering' },
    )
    const retired = await service.updateDepartment(
      actor(), organizationId, engineeringDepartmentId, { status: 'retired' },
    )

    expect(created).toMatchObject({ organizationId, name: 'Operations', status: 'active' })
    expect(renamed).toMatchObject({ organizationId, name: 'Product Engineering' })
    expect(retired).toMatchObject({ organizationId, status: 'retired' })
    expect(repo.createDepartment).toHaveBeenCalledWith(actor(), organizationId, { name: 'Operations' })
    expect(repo.updateDepartment).toHaveBeenNthCalledWith(
      1, actor(), organizationId, engineeringDepartmentId, { name: 'Product Engineering' },
    )
    expect(repo.updateDepartment).toHaveBeenNthCalledWith(
      2, actor(), organizationId, engineeringDepartmentId, { status: 'retired' },
    )
  })

  it('changes organization membership and its explicit default only within the same scope', async () => {
    const repo = repository()
    const service = new OrganizationAdminService(repo)
    const initialRequest = {
      userObjectId: targetObjectId,
      departmentIds: [engineeringDepartmentId],
      defaultDepartmentId: engineeringDepartmentId,
    }
    const changedRequest = {
      userObjectId: targetObjectId,
      departmentIds: [engineeringDepartmentId, operationsDepartmentId],
      defaultDepartmentId: operationsDepartmentId,
    }

    await service.registerMembership(actor(), organizationId, initialRequest)
    const result = await service.registerMembership(actor(), organizationId, changedRequest)

    expect(result).toEqual(membership)
    expect(repo.registerMembership).toHaveBeenNthCalledWith(1, actor(), organizationId, initialRequest)
    expect(repo.registerMembership).toHaveBeenNthCalledWith(2, actor(), organizationId, changedRequest)
  })

  it('rejects a default outside the requested department memberships without persisting', async () => {
    const repo = repository()
    const service = new OrganizationAdminService(repo)

    await expect(service.registerMembership(actor(), organizationId, {
      userObjectId: targetObjectId,
      departmentIds: [engineeringDepartmentId],
      defaultDepartmentId: operationsDepartmentId,
    })).rejects.toMatchObject({ code: 'invalid_scope', statusCode: 400 })
    expect(repo.registerMembership).not.toHaveBeenCalled()
  })

  it('grants supported delegated roles and revokes only the targeted assignment', async () => {
    const repo = repository()
    const service = new OrganizationAdminService(repo)

    const granted = await service.grantRole(actor(), organizationId, {
      userObjectId: targetObjectId,
      role: 'recruiter',
      departmentId: engineeringDepartmentId,
    })
    await service.revokeRole(actor(), organizationId, assignmentId)

    expect(granted).toEqual(recruiterAssignment)
    expect(repo.grantRole).toHaveBeenCalledWith(actor(), organizationId, {
      userObjectId: targetObjectId,
      role: 'recruiter',
      departmentId: engineeringDepartmentId,
    })
    expect(repo.revokeRole).toHaveBeenCalledOnce()
    expect(repo.revokeRole).toHaveBeenCalledWith(actor(), organizationId, assignmentId)
    expect(repo.revokeRole).not.toHaveBeenCalledWith(actor(), organizationId, unrelatedAssignmentId)
  })

  it('denies cross-organization changes and application-wide Admin escalation', async () => {
    const repo = repository()
    const service = new OrganizationAdminService(repo)

    await expect(service.createDepartment(actor(), otherOrganizationId, { name: 'Finance' }))
      .rejects.toMatchObject({ code: 'forbidden', statusCode: 403 })
    await expect(service.registerMembership(actor(), otherOrganizationId, {
      userObjectId: targetObjectId,
      departmentIds: [engineeringDepartmentId],
      defaultDepartmentId: engineeringDepartmentId,
    })).rejects.toMatchObject({ code: 'forbidden', statusCode: 403 })
    await expect(service.grantRole(actor(), organizationId, {
      userObjectId: targetObjectId,
      role: 'admin' as never,
      departmentId: null,
    })).rejects.toMatchObject({ code: 'invalid_scope', statusCode: 400 })
    await expect(service.createOrganization(actor(), {
      name: 'Fabrikam',
      initialDepartmentName: 'General',
    })).rejects.toMatchObject({ code: 'forbidden', statusCode: 403 })

    expect(repo.createDepartment).not.toHaveBeenCalled()
    expect(repo.grantRole).not.toHaveBeenCalled()
    expect(repo.createOrganization).not.toHaveBeenCalled()
  })
})

describe('Stack A Organization Admin HTTP contract', () => {
  it('maps department lifecycle, membership/default, delegated grant, and targeted revocation', async () => {
    const server = await createServer()
    const json = { 'Content-Type': 'application/json' }

    const responses = await Promise.all([
      server.request(`/api/organizations/${organizationId}/departments`, {
        method: 'POST', headers: json, body: JSON.stringify({ name: 'Operations' }),
      }),
      server.request(`/api/organizations/${organizationId}/departments/${engineeringDepartmentId}`, {
        method: 'PATCH', headers: json, body: JSON.stringify({ name: 'Product Engineering' }),
      }),
      server.request(`/api/organizations/${organizationId}/departments/${engineeringDepartmentId}`, {
        method: 'PATCH', headers: json, body: JSON.stringify({ status: 'retired' }),
      }),
      server.request(`/api/organizations/${organizationId}/memberships`, {
        method: 'POST',
        headers: json,
        body: JSON.stringify({
          userObjectId: targetObjectId,
          departmentIds: [engineeringDepartmentId, operationsDepartmentId],
          defaultDepartmentId: operationsDepartmentId,
        }),
      }),
      server.request(`/api/organizations/${organizationId}/role-assignments`, {
        method: 'POST',
        headers: json,
        body: JSON.stringify({
          userObjectId: targetObjectId,
          role: 'recruiter',
          departmentId: engineeringDepartmentId,
        }),
      }),
      server.request(`/api/organizations/${organizationId}/role-assignments/${assignmentId}`, {
        method: 'DELETE',
      }),
    ])

    expect(responses.map(response => response.status)).toEqual([201, 200, 200, 200, 201, 204])
    expect(server.repo.revokeRole).toHaveBeenCalledWith(
      expect.objectContaining({ objectId: actorObjectId, organizationAdminIds: [organizationId], correlationId }),
      organizationId,
      assignmentId,
    )
  })

  it('returns safe correlated denials for foreign scope, invalid defaults, and escalation', async () => {
    const server = await createServer()
    const json = { 'Content-Type': 'application/json' }
    const responses = await Promise.all([
      server.request(`/api/organizations/${otherOrganizationId}/departments`, {
        method: 'POST', headers: json, body: JSON.stringify({ name: 'Finance' }),
      }),
      server.request(`/api/organizations/${otherOrganizationId}/memberships`, {
        method: 'POST',
        headers: json,
        body: JSON.stringify({
          userObjectId: targetObjectId,
          departmentIds: [engineeringDepartmentId],
          defaultDepartmentId: engineeringDepartmentId,
        }),
      }),
      server.request(`/api/organizations/${organizationId}/memberships`, {
        method: 'POST',
        headers: json,
        body: JSON.stringify({
          userObjectId: targetObjectId,
          departmentIds: [engineeringDepartmentId],
          defaultDepartmentId: operationsDepartmentId,
        }),
      }),
      server.request(`/api/organizations/${organizationId}/role-assignments`, {
        method: 'POST',
        headers: json,
        body: JSON.stringify({ userObjectId: targetObjectId, role: 'admin', departmentId: null }),
      }),
      server.request('/api/organizations', {
        method: 'POST',
        headers: json,
        body: JSON.stringify({ name: 'Fabrikam', initialDepartmentName: 'General' }),
      }),
    ])
    const bodies = await Promise.all(responses.map(response => response.json() as Promise<Record<string, string>>))

    expect(responses.map(response => response.status)).toEqual([403, 403, 400, 400, 403])
    expect(bodies.map(body => body.error)).toEqual([
      'forbidden', 'forbidden', 'invalid_scope', 'invalid_scope', 'forbidden',
    ])
    for (const [index, response] of responses.entries()) {
      expect(response.headers.get('x-correlation-id')).toBe(correlationId)
      expect(bodies[index]).toMatchObject({ message: expect.any(String), correlationId })
    }
    expect(server.repo.createDepartment).not.toHaveBeenCalled()
    expect(server.repo.registerMembership).not.toHaveBeenCalled()
    expect(server.repo.grantRole).not.toHaveBeenCalled()
    expect(server.repo.createOrganization).not.toHaveBeenCalled()
  })

  it('maps organization conflicts to the canonical correlated version conflict', async () => {
    const repo = repository()
    vi.mocked(repo.createDepartment).mockRejectedValue({
      code: 'conflict',
      message: 'The department conflicts with current organization state.',
    })
    const server = await createServer('organization_admin', repo)

    const response = await server.request(`/api/organizations/${organizationId}/departments`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'Operations' }),
    })
    const body = await response.json() as Record<string, string>

    expect(response.status).toBe(409)
    expect(body).toEqual({
      error: 'version_conflict',
      message: 'The department conflicts with current organization state.',
      correlationId,
    })
    expect(response.headers.get('x-correlation-id')).toBe(correlationId)
  })
})