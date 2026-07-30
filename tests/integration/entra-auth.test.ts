import { once } from 'node:events'
import type { AddressInfo } from 'node:net'
import express from 'express'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { RoleAssignment, RoleGroupMapping } from '../../src/types/index.js'
import { createAuthMiddleware } from '../../server/middleware/auth.js'
import { createAuthRouter } from '../../server/routes/auth.js'
import {
  AuthorizationError,
  createAuthorizationResolver,
  type AuthorizationMembership,
  type AuthorizationRepositories,
} from '../../server/services/authorization.js'
import { EntraTokenError, type NormalizedEntraClaims } from '../../server/services/entra-token.js'

const tenantId = '11111111-1111-4111-8111-111111111111'
const assignedObjectId = '22222222-2222-4222-8222-222222222222'
const unassignedObjectId = '33333333-3333-4333-8333-333333333333'
const disabledObjectId = '44444444-4444-4444-8444-444444444444'
const scopeObjectId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const organizationId = '55555555-5555-4555-8555-555555555555'
const departmentId = '66666666-6666-4666-8666-666666666666'
const missingDepartmentId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const issuedAt = new Date('2026-07-25T12:00:00.000Z')

const membership: AuthorizationMembership = {
  organizationId,
  organizationName: 'Analytical Engines',
  departments: [{ departmentId, departmentName: 'Engineering' }],
}

const assignedUser = {
  userId: assignedObjectId,
  username: 'ada@example.com',
  role: 'recruiter' as const,
  authenticationProvider: 'entra' as const,
  entraTenantId: tenantId,
  entraObjectId: assignedObjectId,
  isActive: true,
  fullName: 'Ada Lovelace',
  email: 'ada@example.com',
  createdAt: issuedAt.toISOString(),
}

const assignment: RoleAssignment = {
  assignmentId: '77777777-7777-4777-8777-777777777777',
  userId: assignedObjectId,
  tenantId,
  userObjectId: assignedObjectId,
  role: 'recruiter',
  organizationId,
  departmentId,
  source: 'delegated',
  status: 'active',
  effectiveAt: issuedAt.toISOString(),
  createdAt: issuedAt.toISOString(),
  updatedAt: issuedAt.toISOString(),
  updatedBy: 'test',
}

function claims(objectId = assignedObjectId): NormalizedEntraClaims {
  return {
    tenantId,
    objectId,
    authorizedClientId: '88888888-8888-4888-8888-888888888888',
    scopes: ['access_as_user'],
    roles: [],
    groups: [],
    username: `${objectId.slice(0, 8)}@example.com`,
    fullName: 'Ada Lovelace',
    email: 'ada@example.com',
    issuedAt,
    expiresAt: new Date(issuedAt.getTime() + 60 * 60 * 1000),
  }
}

function createRepositories(): AuthorizationRepositories {
  return {
    users: {
      getByEntraIdentity: vi.fn(async (_tenant, objectId) => {
        if (objectId === assignedObjectId) return assignedUser
        if (objectId === unassignedObjectId) return { ...assignedUser, userId: objectId, entraObjectId: objectId }
        if (objectId === disabledObjectId) return { ...assignedUser, userId: objectId, entraObjectId: objectId, isActive: false }
        if (objectId === scopeObjectId) return { ...assignedUser, userId: objectId, entraObjectId: objectId }
        return undefined
      }),
      upsertEntraProfile: vi.fn(async profile => ({
        ...assignedUser,
        ...profile,
        isActive: profile.entraObjectId === disabledObjectId ? false : true,
      })),
      update: vi.fn(async () => undefined),
    },
    organizations: {
      getAuthorizationMemberships: vi.fn(async userId => userId === unassignedObjectId ? [membership] : [membership]),
    },
    roleAssignments: {
      getActiveForIdentity: vi.fn(async (_tenant, objectId) => {
        if (objectId === unassignedObjectId) return []
        return [{
          ...assignment,
          userId: objectId,
          userObjectId: objectId,
          departmentId: objectId === scopeObjectId ? missingDepartmentId : departmentId,
        }]
      }),
      getGroupMappingsByIds: vi.fn(async () => [] as RoleGroupMapping[]),
    },
  }
}

async function createTestServer() {
  const repositories = createRepositories()
  const resolver = createAuthorizationResolver(repositories, { bootstrapObjectId: assignedObjectId })
  const audit = {
    appendAuthorizationEvent: vi.fn(async () => ({ eventId: 'event-1' })),
  }
  const validator = {
    validate: vi.fn(async (token: string) => {
      if (token === 'opaque-wrong-tenant-token') {
        throw new EntraTokenError('wrong_tenant', 'internal tenant detail')
      }
      if (token === 'opaque-expired-token') {
        throw new EntraTokenError('token_stale', 'internal stale detail')
      }
      if (token === 'opaque-unassigned-token') return claims(unassignedObjectId)
      if (token === 'opaque-disabled-token') return claims(disabledObjectId)
      if (token === 'opaque-scope-token') return claims(scopeObjectId)
      return claims()
    }),
  }
  const authMiddleware = createAuthMiddleware({
    mode: 'entra',
    tokenValidator: validator,
    authorizationResolver: resolver,
    audit,
  })
  const app = express()
  app.use(express.json())
  app.use('/api/auth', createAuthRouter({
    mode: 'entra',
    authMiddleware,
    audit,
    users: repositories.users,
  }))
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  return {
    audit,
    repositories,
    request: (path: string, init: RequestInit = {}) => fetch(`http://127.0.0.1:${port}${path}`, init),
    close: () => new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve())),
  }
}

const closeCallbacks: Array<() => Promise<void>> = []

afterEach(async () => {
  await Promise.all(closeCallbacks.splice(0).map(close => close()))
})

async function setup() {
  const server = await createTestServer()
  closeCallbacks.push(server.close)
  return server
}

function bearer(token: string): RequestInit {
  return { headers: { Authorization: `Bearer ${token}` } }
}

describe('Stack A Entra authentication contract', () => {
  it('returns the exact authorization context for an assigned identity', async () => {
    const server = await setup()

    const response = await server.request('/api/auth/me', bearer('assigned'))

    expect(response.status).toBe(200)
    expect(response.headers.get('x-correlation-id')).toMatch(/^[0-9a-f-]{36}$/i)
    await expect(response.json()).resolves.toEqual({
      userId: assignedObjectId,
      tenantId,
      objectId: assignedObjectId,
      username: claims().username,
      fullName: assignedUser.fullName,
      email: assignedUser.email,
      globalRole: null,
      memberships: [membership],
      authorizations: [{
        role: 'recruiter',
        roleLabel: 'Recruiter',
        organizationId,
        departmentId,
        assignmentSource: 'delegated',
      }],
      tokenIssuedAt: issuedAt.toISOString(),
      refreshRequiredAt: new Date(issuedAt.getTime() + 15 * 60 * 1000).toISOString(),
    })
    expect(server.repositories.users.update).toHaveBeenCalledWith(
      assignedObjectId,
      expect.objectContaining({ lastLogin: expect.any(String) }),
    )
    expect(server.audit.appendAuthorizationEvent).toHaveBeenCalledWith(
      assignedObjectId,
      'auth.login.succeeded',
      assignedObjectId,
      expect.objectContaining({ result: 'succeeded', tenantId }),
      response.headers.get('x-correlation-id'),
    )
    expect(JSON.stringify(server.audit.appendAuthorizationEvent.mock.calls)).not.toContain('Bearer')
  })

  it.each([
    ['opaque-unassigned-token', 403, 'assignment_missing', 'auth.login.denied'],
    ['opaque-disabled-token', 403, 'identity_disabled', 'auth.login.denied'],
    ['opaque-wrong-tenant-token', 401, 'wrong_tenant', 'auth.login.denied'],
    ['opaque-expired-token', 401, 'token_stale', 'auth.token.stale'],
    ['opaque-scope-token', 403, 'membership_missing', 'auth.scope.unmapped'],
  ])('denies %s with a canonical safe error', async (token, status, error, auditAction) => {
    const server = await setup()

    const response = await server.request('/api/auth/me', bearer(token))
    const body = await response.json() as Record<string, string>

    expect(response.status).toBe(status)
    expect(body).toEqual({
      error,
      message: expect.any(String),
      correlationId: response.headers.get('x-correlation-id'),
    })
    expect(body.message).not.toContain('internal')
    expect(JSON.stringify(body)).not.toContain(token)
    expect(server.audit.appendAuthorizationEvent).toHaveBeenCalledWith(
      expect.any(String),
      auditAction,
      expect.any(String),
      expect.objectContaining({ result: 'denied', reasonCode: error }),
      response.headers.get('x-correlation-id'),
    )
    expect(JSON.stringify(server.audit.appendAuthorizationEvent.mock.calls)).not.toContain(token)
  })

  it('rejects malformed bearer input without exposing it', async () => {
    const server = await setup()

    const response = await server.request('/api/auth/me', {
      headers: { Authorization: 'Basic secret-value' },
    })
    const body = await response.json() as Record<string, string>

    expect(response.status).toBe(401)
    expect(body.error).toBe('invalid_token')
    expect(JSON.stringify(body)).not.toContain('secret-value')
  })

  it('audits logout without token data and returns 204', async () => {
    const server = await setup()

    const response = await server.request('/api/auth/logout', {
      method: 'POST',
      ...bearer('assigned'),
    })

    expect(response.status).toBe(204)
    expect(await response.text()).toBe('')
    expect(server.audit.appendAuthorizationEvent).toHaveBeenCalledWith(
      assignedObjectId,
      'auth.logout',
      assignedObjectId,
      expect.objectContaining({ result: 'succeeded' }),
      response.headers.get('x-correlation-id'),
    )
    expect(JSON.stringify(server.audit.appendAuthorizationEvent.mock.calls)).not.toContain('assigned')
  })

  it.each(['/login', '/change-password', '/request-password-reset'])(
    'does not expose password endpoint %s in Entra mode',
    async path => {
      const server = await setup()

      const response = await server.request(`/api/auth${path}`, { method: 'POST' })

      expect([404, 405]).toContain(response.status)
      expect(response.headers.get('x-correlation-id')).toMatch(/^[0-9a-f-]{36}$/i)
    },
  )
})

describe('authorization resolver invariants', () => {
  it('requires enabled mappings plus matching roles and groups for group assignments', async () => {
    const repositories = createRepositories()
    const groupAssignment = {
      ...assignment,
      source: 'group' as const,
      roleGroupMappingId: '99999999-9999-4999-8999-999999999999',
    }
    vi.mocked(repositories.roleAssignments.getActiveForIdentity).mockResolvedValue([groupAssignment])
    vi.mocked(repositories.roleAssignments.getGroupMappingsByIds).mockResolvedValue([])
    const resolver = createAuthorizationResolver(repositories, { bootstrapObjectId: assignedObjectId })

    await expect(resolver.resolve({ ...claims(), roles: ['recruiter'], groups: [] }))
      .rejects.toMatchObject({ code: 'assignment_revoked', statusCode: 403 })
  })

  it('does not require roles or groups for delegated assignments', async () => {
    const resolver = createAuthorizationResolver(createRepositories(), { bootstrapObjectId: assignedObjectId })

    const context = await resolver.resolve(claims())

    expect(context.authorizations).toHaveLength(1)
    expect(context.authorizations[0].assignmentSource).toBe('delegated')
  })

  it.each(['organization_admin', 'business_panel'] as const)(
    'accepts an organization-wide %s assignment with parent membership',
    async role => {
      const repositories = createRepositories()
      vi.mocked(repositories.roleAssignments.getActiveForIdentity).mockResolvedValue([{
        ...assignment,
        role,
        departmentId: undefined,
      }])
      const resolver = createAuthorizationResolver(repositories)

      const context = await resolver.resolve(claims())

      expect(context.authorizations).toEqual([expect.objectContaining({
        role,
        organizationId,
        departmentId: null,
      })])
    },
  )

  it('uses the immutable Entra object ID for a first-time profile', async () => {
    const repositories = createRepositories()
    vi.mocked(repositories.users.getByEntraIdentity).mockResolvedValue(undefined)
    const resolver = createAuthorizationResolver(repositories)

    await resolver.resolve(claims())

    expect(repositories.users.upsertEntraProfile).toHaveBeenCalledWith(
      expect.objectContaining({ userId: assignedObjectId }),
    )
  })

  it('accepts a group assignment only with an enabled matching role and group mapping', async () => {
    const repositories = createRepositories()
    const mappingId = '99999999-9999-4999-8999-999999999999'
    const groupObjectId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
    vi.mocked(repositories.roleAssignments.getActiveForIdentity).mockResolvedValue([{
      ...assignment,
      source: 'group',
      roleGroupMappingId: mappingId,
    }])
    vi.mocked(repositories.roleAssignments.getGroupMappingsByIds).mockResolvedValue([{
      mappingId,
      tenantId,
      groupObjectId,
      role: 'recruiter',
      organizationId,
      departmentId,
      enabled: true,
      createdAt: issuedAt.toISOString(),
      updatedAt: issuedAt.toISOString(),
      updatedBy: 'test',
    }])
    const resolver = createAuthorizationResolver(repositories)

    const context = await resolver.resolve({
      ...claims(),
      roles: ['recruiter'],
      groups: [groupObjectId],
    })

    expect(context.authorizations[0].assignmentSource).toBe('group')
  })

  it('requires the configured object ID and Admin token role for bootstrap', async () => {
    const repositories = createRepositories()
    vi.mocked(repositories.roleAssignments.getActiveForIdentity).mockResolvedValue([{
      ...assignment,
      role: 'admin',
      organizationId: undefined,
      departmentId: undefined,
      source: 'bootstrap',
    }])
    const resolver = createAuthorizationResolver(repositories, { bootstrapObjectId: assignedObjectId })

    await expect(resolver.resolve(claims()))
      .rejects.toMatchObject({ code: 'role_missing' })

    const context = await resolver.resolve({ ...claims(), roles: ['admin'] })
    expect(context.globalRole).toBe('admin')
  })

  it('uses the highest role only within the same resource scope', async () => {
    const repositories = createRepositories()
    vi.mocked(repositories.roleAssignments.getActiveForIdentity).mockResolvedValue([
      assignment,
      {
        ...assignment,
        assignmentId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
        role: 'business_panel',
      },
    ])
    const resolver = createAuthorizationResolver(repositories)

    const context = await resolver.resolve(claims())

    expect(context.authorizations).toEqual([expect.objectContaining({
      role: 'recruiter',
      organizationId,
      departmentId,
    })])
  })
})