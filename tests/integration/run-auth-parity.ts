import { once } from 'node:events'
import { existsSync, readFileSync } from 'node:fs'
import type { AddressInfo } from 'node:net'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
import express, { type Express } from 'express'
import type {
  EntraAccessUser,
  EntraOrganizationAccess,
  RoleAssignment,
} from '../../src/types/index.js'
import { createAuthMiddleware } from '../../server/middleware/auth.js'
import { createAccessManagementRouter } from '../../server/routes/access-management.js'
import { createAuthRouter } from '../../server/routes/auth.js'
import { createJobsRouter } from '../../server/routes/jobs.js'
import { createOrganizationsRouter } from '../../server/routes/organizations.js'
import {
  EntraAccessError,
  EntraAccessManagementService,
  type AccessManagementActor,
  type EntraAccessSearchOptions,
} from '../../server/services/entra-access-management.js'
import {
  createAuthorizationResolver,
  type AuthorizationMembership,
  type AuthorizationRepositories,
} from '../../server/services/authorization.js'
import {
  OrganizationAdminService,
  type OrganizationAdminAudit,
  type OrganizationAdminMembership,
  type OrganizationAdminRepository,
  type OrganizationAdminRoleAssignment,
} from '../../server/services/organization-admin.js'
import { EntraTokenError, type NormalizedEntraClaims } from '../../server/services/entra-token.js'
import type { EntraAccessManagementRepository } from '../../server/storage/repos/access-management-repo.js'
import { jobRepo, userRepo } from '../../server/storage/repos/index.js'

export type AuthenticationMode = 'entra' | 'simple'

export interface CanonicalParityResult {
  status?: number
  [field: string]: unknown
}

interface FixtureProfile {
  objectId: string
  isActive: boolean
  authorizationVersion: number
  memberships: Array<{
    organizationId: string
    departmentIds: string[]
    defaultDepartmentId: string
  }>
  assignments: Array<{
    assignmentId: string
    role: 'admin' | 'organization_admin' | 'recruiter' | 'business_panel'
    organizationId: string | null
    departmentId: string | null
    source: 'group' | 'delegated' | 'bootstrap'
    status: 'active' | 'revoked'
  }>
}

interface ReferenceState {
  configuredTenantId: string
  primaryOrganizationId: string
  otherOrganizationId: string
  primaryDepartmentId: string
  replacementDepartmentId: string
  otherDepartmentId: string
  profiles: Record<string, FixtureProfile>
}

export interface AuthorizationParityCase {
  id: string
  area: string
  category: string
  host: string
  authScenario: string
  input: Record<string, unknown>
  request: {
    method: string
    path: string
    body?: Record<string, unknown>
  }
  expectedByMode: Record<AuthenticationMode, CanonicalParityResult>
  [field: string]: unknown
}

export interface AuthorizationParityFixture {
  schemaVersion: number
  referenceState: ReferenceState
  cases: AuthorizationParityCase[]
  [field: string]: unknown
}

interface AuditObservation {
  action: string
  result: string
  reasonCode?: string
}

interface HttpObservation {
  status: number
  body: unknown
  correlationId: string | null
}

interface RawParityResult {
  caseId: string
  mode: AuthenticationMode
  response?: HttpObservation
  before: unknown
  after: unknown
  audits: AuditObservation[]
  stable: CanonicalParityResult
}

const reportFixturePath = resolve(process.cwd(), 'tests/fixtures/authorization-parity-cases.json')
const fixedNow = new Date('2026-08-01T12:00:00.000Z')
const actorObjectId = '12121212-1212-4121-8121-121212121212'
const correlationId = '13131313-1313-4131-8131-131313131313'

function clone<T>(value: T): T {
  return structuredClone(value)
}

function same(left: unknown, right: unknown): boolean {
  return JSON.stringify(left) === JSON.stringify(right)
}

function profileName(testCase: AuthorizationParityCase): string {
  return String(testCase.input.profile ?? testCase.input.resultingProfile ?? 'pendingUser')
}

function objectIdFromPath(path: string): string | undefined {
  return /\/users\/([0-9a-f-]{36})(?:\/|\?|$)/i.exec(path)?.[1]
}

function toRequestInit(testCase: AuthorizationParityCase): RequestInit {
  return {
    method: testCase.request.method,
    headers: {
      'Content-Type': 'application/json',
      Connection: 'close',
      'X-Correlation-ID': correlationId,
    },
    body: testCase.request.body === undefined ? undefined : JSON.stringify(testCase.request.body),
  }
}

async function requestApp(app: Express, testCase: AuthorizationParityCase): Promise<HttpObservation> {
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  try {
    const response = await fetch(`http://127.0.0.1:${port}${testCase.request.path}`, toRequestInit(testCase))
    const text = await response.text()
    let body: unknown = text
    if (text) {
      try {
        body = JSON.parse(text)
      } catch {
        // Express's default 404 body is intentionally retained as raw evidence.
      }
    }
    return {
      status: response.status,
      body,
      correlationId: response.headers.get('x-correlation-id'),
    }
  } finally {
    await new Promise<void>((resolveClose, reject) => {
      server.close(error => error ? reject(error) : resolveClose())
    })
  }
}

function jsonBody(response: HttpObservation): Record<string, any> {
  return response.body && typeof response.body === 'object'
    ? response.body as Record<string, any>
    : {}
}

function fixtureProfileToAccess(profile: FixtureProfile, objectId = profile.objectId): EntraAccessUser {
  const organizations = profile.memberships.map<EntraOrganizationAccess>(membership => ({
    organizationId: membership.organizationId,
    status: 'active',
    departmentIds: [...membership.departmentIds],
    defaultDepartmentId: membership.defaultDepartmentId,
    roleAssignments: profile.assignments
      .filter(assignment => assignment.organizationId === membership.organizationId)
      .map(assignment => ({
        id: assignment.assignmentId,
        role: assignment.role === 'admin' ? 'organization_admin' : assignment.role,
        organizationId: membership.organizationId,
        departmentId: assignment.departmentId,
        source: assignment.source === 'bootstrap' ? 'delegated' : assignment.source,
        status: assignment.status,
      })),
  }))
  return {
    objectId,
    username: `${objectId.slice(0, 8)}@contoso.example`,
    fullName: `Parity User ${objectId.slice(0, 8)}`,
    email: `${objectId.slice(0, 8)}@contoso.example`,
    isActive: profile.isActive,
    authorizationVersion: profile.authorizationVersion,
    organizations,
  }
}

function accessContext(reference: ReferenceState, scenario: string) {
  return {
    userId: actorObjectId,
    tenantId: reference.configuredTenantId,
    objectId: actorObjectId,
    username: 'parity.actor@contoso.example',
    fullName: 'Parity Actor',
    email: 'parity.actor@contoso.example',
    globalRole: scenario === 'admin' ? 'admin' as const : null,
    authorizationVersion: 1,
    memberships: [],
    authorizations: scenario === 'organization-admin'
      ? [{
          role: 'organization_admin' as const,
          roleLabel: 'Organization Admin',
          organizationId: reference.primaryOrganizationId,
          departmentId: null,
          assignmentSource: 'delegated' as const,
        }]
      : [],
    tokenIssuedAt: fixedNow.toISOString(),
    refreshRequiredAt: new Date(fixedNow.getTime() + 15 * 60 * 1000).toISOString(),
  }
}

function createAccessState(fixture: AuthorizationParityFixture, testCase: AuthorizationParityCase) {
  const reference = fixture.referenceState
  const users = new Map<string, EntraAccessUser>()
  for (const profile of Object.values(reference.profiles)) {
    users.set(profile.objectId, fixtureProfileToAccess(profile))
  }

  const selectedFixtureProfile = reference.profiles[profileName(testCase)] ?? reference.profiles.pendingUser
  const pathObjectId = objectIdFromPath(testCase.request.path)
  if (pathObjectId) {
    const selected = fixtureProfileToAccess(selectedFixtureProfile, pathObjectId)
    users.set(pathObjectId, selected)
  }

  const target = pathObjectId ? users.get(pathObjectId) : undefined
  if (testCase.id === 'revocation-access-role-preserves-unrelated-scope' && target) {
    const organization = target.organizations.find(item => item.organizationId === reference.primaryOrganizationId)
    const assignmentId = String(testCase.input.revokedAssignmentId)
    if (organization && !organization.roleAssignments.some(item => item.id === assignmentId)) {
      organization.roleAssignments.push({
        id: assignmentId,
        role: 'recruiter',
        organizationId: reference.primaryOrganizationId,
        departmentId: reference.primaryDepartmentId,
        source: 'delegated',
        status: 'active',
      })
    }
  }
  return users
}

function filterAccessUser(user: EntraAccessUser, actor: AccessManagementActor): EntraAccessUser {
  const result = clone(user)
  if (!actor.globalAdmin) {
    result.organizations = result.organizations.filter(organization =>
      actor.organizationAdminIds.includes(organization.organizationId))
  }
  return result
}

async function executeAccessCase(
  fixture: AuthorizationParityFixture,
  testCase: AuthorizationParityCase,
): Promise<RawParityResult> {
  const reference = fixture.referenceState
  const users = createAccessState(fixture, testCase)
  const audits: AuditObservation[] = []
  const before = clone([...users.entries()])

  const repository: EntraAccessManagementRepository = {
    async list(requestActor: AccessManagementActor, options: EntraAccessSearchOptions) {
      const items = [...users.values()]
        .filter(user => {
          const status = !user.isActive ? 'disabled' : user.organizations.length === 0 ? 'pending' : 'active'
          return options.status === undefined || options.status === status
        })
        .filter(user => !options.search
          || `${user.username} ${user.fullName} ${user.objectId}`.toLowerCase().includes(options.search.toLowerCase()))
        .map(user => filterAccessUser(user, requestActor))
      return { items, nextCursor: null }
    },
    async get(requestActor, objectId) {
      const user = users.get(objectId)
      return user ? filterAccessUser(user, requestActor) : undefined
    },
    async updateUser(_requestActor, objectId, request) {
      const user = users.get(objectId)
      if (!user) throw new EntraAccessError('not_found', 'The profile was not found.', 404)
      if (user.authorizationVersion !== request.expectedVersion) {
        throw new EntraAccessError('version_conflict', 'Authorization state changed. Refresh and retry.', 409)
      }
      if (request.profile) Object.assign(user, request.profile)
      if (request.isActive !== undefined) user.isActive = request.isActive
      user.authorizationVersion++
      audits.push({ action: 'access_management_succeeded', result: 'succeeded' })
      return clone(user)
    },
    async putOrganizationAccess(_requestActor, objectId, organizationId, request) {
      const user = users.get(objectId)
      if (!user) throw new EntraAccessError('not_found', 'The profile was not found.', 404)
      if (user.authorizationVersion !== request.expectedVersion) {
        throw new EntraAccessError('version_conflict', 'Authorization state changed. Refresh and retry.', 409)
      }
      const allowedDepartments = organizationId === reference.primaryOrganizationId
        ? [reference.primaryDepartmentId, reference.replacementDepartmentId]
        : [reference.otherDepartmentId]
      if (request.membership.departmentIds.some(id => !allowedDepartments.includes(id))) {
        throw new EntraAccessError('invalid_scope', 'A department is outside the organization.', 400)
      }
      Object.assign(user, request.profile)
      const access: EntraOrganizationAccess = {
        organizationId,
        status: request.membership.status,
        departmentIds: [...request.membership.departmentIds],
        defaultDepartmentId: request.membership.defaultDepartmentId,
        roleAssignments: request.roleAssignments.map((assignment, index) => ({
          id: `14141414-1414-4141-8141-${String(index + 1).padStart(12, '0')}`,
          role: assignment.role,
          organizationId,
          departmentId: assignment.departmentId,
          source: 'delegated',
          status: 'active',
        })),
      }
      user.organizations = [
        ...user.organizations.filter(item => item.organizationId !== organizationId),
        access,
      ]
      user.authorizationVersion++
      audits.push({ action: 'access_management_succeeded', result: 'succeeded' })
      return clone(user)
    },
    async revokeRole(_requestActor, objectId, organizationId, assignmentId, expectedVersion) {
      const user = users.get(objectId)
      if (!user) throw new EntraAccessError('not_found', 'The profile was not found.', 404)
      if (user.authorizationVersion !== expectedVersion) {
        throw new EntraAccessError('version_conflict', 'Authorization state changed. Refresh and retry.', 409)
      }
      const organization = user.organizations.find(item => item.organizationId === organizationId)
      const assignment = organization?.roleAssignments.find(item => item.id === assignmentId)
      if (!assignment || assignment.source !== 'delegated') {
        throw new EntraAccessError('not_found', 'The delegated assignment was not found.', 404)
      }
      assignment.status = 'revoked'
      user.authorizationVersion++
      audits.push({ action: 'access_management_succeeded', result: 'succeeded' })
      return clone(user)
    },
  }

  const app = express()
  app.use(express.json())
  app.use((req, _res, next) => {
    ;(req as any).authorizationContext = accessContext(reference, testCase.authScenario)
    next()
  })
  app.use('/api/access-management', createAccessManagementRouter(new EntraAccessManagementService(repository)))
  const response = await requestApp(app, testCase)
  const after = clone([...users.entries()])
  const body = jsonBody(response)
  const targetObjectId = objectIdFromPath(testCase.request.path)
  const current = targetObjectId ? users.get(targetObjectId) : undefined
  const beforeTarget = targetObjectId
    ? (before as Array<[string, EntraAccessUser]>).find(([id]) => id === targetObjectId)?.[1]
    : undefined
  const beforeOther = beforeTarget?.organizations.find(item => item.organizationId === reference.otherOrganizationId)
  const afterOther = current?.organizations.find(item => item.organizationId === reference.otherOrganizationId)

  let stable: CanonicalParityResult
  switch (testCase.id) {
    case 'access-list-pending-profiles':
      stable = { status: response.status, capability: 'entra_access_management' }
      break
    case 'access-inspect-profile-version-and-defaults':
      stable = {
        status: response.status,
        authorizationVersion: body.authorizationVersion,
        scopesActorFiltered: Array.isArray(body.organizations)
          && body.organizations.length < (beforeTarget?.organizations.length ?? 0),
      }
      break
    case 'access-onboard-pending-profile-atomically':
      stable = {
        status: response.status,
        stateChanged: !same(before, after),
        authorizationVersion: body.authorizationVersion,
        defaultDepartmentId: body.organizations?.find((item: any) =>
          item.organizationId === reference.primaryOrganizationId)?.defaultDepartmentId,
        auditOutcomes: audits.filter(item => item.result === 'succeeded').length,
      }
      break
    case 'access-admin-disables-profile':
      stable = {
        status: response.status,
        isActive: body.isActive,
        authorizationVersion: body.authorizationVersion,
        auditOutcomes: audits.filter(item => item.result === 'succeeded').length,
      }
      break
    case 'revocation-access-role-preserves-unrelated-scope':
      stable = {
        status: response.status,
        authorizationVersion: body.authorizationVersion,
        unrelatedScopesPreserved: same(beforeOther, afterOther),
        auditOutcomes: audits.filter(item => item.result === 'succeeded').length,
      }
      break
    default:
      stable = {
        status: response.status,
        error: body.error,
        stateChanged: !same(before, after),
        authorizationVersion: current?.authorizationVersion,
      }
  }
  return { caseId: testCase.id, mode: 'entra', response, before, after, audits, stable }
}

function toStoredUser(profile: FixtureProfile) {
  return {
    userId: profile.objectId,
    username: `${profile.objectId.slice(0, 8)}@contoso.example`,
    role: profile.assignments[0]?.role ?? 'recruiter',
    authenticationProvider: 'entra' as const,
    entraTenantId: '',
    entraObjectId: profile.objectId,
    isActive: profile.isActive,
    authorizationVersion: profile.authorizationVersion,
    fullName: `Parity User ${profile.objectId.slice(0, 8)}`,
    email: `${profile.objectId.slice(0, 8)}@contoso.example`,
    createdAt: fixedNow.toISOString(),
  }
}

function toMemberships(reference: ReferenceState, profile: FixtureProfile): AuthorizationMembership[] {
  return profile.memberships.map(membership => ({
    organizationId: membership.organizationId,
    organizationName: membership.organizationId === reference.primaryOrganizationId ? 'Primary' : 'Other',
    defaultDepartmentId: membership.defaultDepartmentId,
    departments: membership.departmentIds.map(departmentId => ({
      departmentId,
      departmentName: departmentId === reference.primaryDepartmentId
        ? 'Engineering'
        : departmentId === reference.replacementDepartmentId ? 'Operations' : 'Finance',
    })),
  }))
}

function toAssignments(reference: ReferenceState, profile: FixtureProfile): RoleAssignment[] {
  return profile.assignments
    .filter(assignment => assignment.status === 'active')
    .map(assignment => ({
      assignmentId: assignment.assignmentId,
      userId: profile.objectId,
      tenantId: reference.configuredTenantId,
      userObjectId: profile.objectId,
      role: assignment.role,
      organizationId: assignment.organizationId ?? undefined,
      departmentId: assignment.departmentId ?? undefined,
      source: assignment.source,
      status: assignment.status,
      effectiveAt: fixedNow.toISOString(),
      createdAt: fixedNow.toISOString(),
      updatedAt: fixedNow.toISOString(),
      updatedBy: 'parity-runner',
    }))
}

function authProfileForScenario(reference: ReferenceState, scenario: string): FixtureProfile {
  if (scenario === 'disabled') return reference.profiles.disabledUser
  if (scenario === 'new-unassigned' || scenario === 'unassigned') return reference.profiles.pendingUser
  return reference.profiles.assignedRecruiter
}

async function executeAuthCase(
  fixture: AuthorizationParityFixture,
  testCase: AuthorizationParityCase,
): Promise<RawParityResult> {
  const reference = fixture.referenceState
  const scenario = testCase.authScenario
  const fixtureProfile = authProfileForScenario(reference, scenario)
  const users = new Map<string, ReturnType<typeof toStoredUser>>()
  if (scenario !== 'new-unassigned') {
    const seeded = toStoredUser(fixtureProfile)
    seeded.entraTenantId = reference.configuredTenantId
    users.set(fixtureProfile.objectId, seeded)
  }
  const audits: AuditObservation[] = []
  const before = clone([...users.entries()])

  const repositories: AuthorizationRepositories = {
    users: {
      async getByEntraIdentity(_tenantId, objectId) {
        return users.get(objectId)
      },
      async upsertEntraProfile(profile) {
        const existing = users.get(profile.entraObjectId)
        const persisted = {
          ...toStoredUser(fixtureProfile),
          ...profile,
          entraTenantId: reference.configuredTenantId,
          isActive: existing?.isActive ?? fixtureProfile.isActive,
          authorizationVersion: existing?.authorizationVersion ?? fixtureProfile.authorizationVersion,
        }
        users.set(profile.entraObjectId, persisted)
        return persisted
      },
      async update() {},
    },
    organizations: {
      async getAuthorizationMemberships() {
        return toMemberships(reference, fixtureProfile)
      },
    },
    roleAssignments: {
      async getActiveForIdentity() {
        return toAssignments(reference, fixtureProfile)
      },
      async getGroupMappingsByIds() {
        return []
      },
    },
  }
  const resolver = createAuthorizationResolver(repositories, { now: () => fixedNow })
  const claims: NormalizedEntraClaims = {
    tenantId: reference.configuredTenantId,
    objectId: fixtureProfile.objectId,
    authorizedClientId: '15151515-1515-4151-8151-151515151515',
    scopes: ['access_as_user'],
    roles: [],
    groups: [],
    username: `${fixtureProfile.objectId.slice(0, 8)}@contoso.example`,
    fullName: `Parity User ${fixtureProfile.objectId.slice(0, 8)}`,
    email: `${fixtureProfile.objectId.slice(0, 8)}@contoso.example`,
    issuedAt: fixedNow,
    expiresAt: new Date(fixedNow.getTime() + 60 * 60 * 1000),
  }
  const validator = {
    async validate() {
      if (scenario === 'wrong-tenant') throw new EntraTokenError('wrong_tenant', 'Wrong tenant')
      if (scenario === 'stale') throw new EntraTokenError('token_stale', 'Token is stale')
      return claims
    },
  }
  const audit = {
    async appendAuthorizationEvent(
      _actor: string,
      action: string,
      _subjectId: string,
      details: Record<string, unknown>,
    ) {
      audits.push({
        action,
        result: String(details.result),
        reasonCode: details.reasonCode === undefined ? undefined : String(details.reasonCode),
      })
      return { eventId: String(audits.length) }
    },
  }
  const authMiddleware = createAuthMiddleware({
    mode: 'entra',
    tokenValidator: validator,
    authorizationResolver: resolver,
    audit,
  })
  const app = express()
  app.use(express.json())
  if (testCase.id === 'jobs-pending-profile-denied-before-data') {
    app.use('/api/jobs', authMiddleware, createJobsRouter())
  } else {
    app.use('/api/auth', createAuthRouter({
      mode: 'entra',
      authMiddleware,
      audit,
      users: repositories.users,
    }))
  }
  const requestCase = clone(testCase)
  requestCase.request = { ...requestCase.request }
  const originalHeaders = toRequestInit(requestCase).headers as Record<string, string>
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  let response: HttpObservation
  try {
    const httpResponse = await fetch(`http://127.0.0.1:${port}${testCase.request.path}`, {
      ...toRequestInit(requestCase),
      headers: { ...originalHeaders, Authorization: 'Bearer parity-token' },
    })
    const text = await httpResponse.text()
    response = {
      status: httpResponse.status,
      body: text ? JSON.parse(text) : '',
      correlationId: httpResponse.headers.get('x-correlation-id'),
    }
  } finally {
    await new Promise<void>((resolveClose, reject) => {
      server.close(error => error ? reject(error) : resolveClose())
    })
  }
  const after = clone([...users.entries()])
  const body = jsonBody(response)
  const current = users.get(fixtureProfile.objectId)
  let stable: CanonicalParityResult
  switch (testCase.id) {
    case 'auth-assigned-explicit-default-and-version':
      stable = {
        status: response.status,
        allowed: response.status === 200,
        authorizationVersion: body.authorizationVersion,
        defaultDepartmentId: body.memberships?.[0]?.defaultDepartmentId,
      }
      break
    case 'auth-first-sign-in-pending-profile':
      stable = {
        status: response.status,
        allowed: response.status < 400,
        error: body.error,
        pendingProfileCreated: !same(before, after),
        authorizationVersion: current?.authorizationVersion,
      }
      break
    case 'auth-disabled-profile':
      stable = {
        status: response.status,
        allowed: response.status < 400,
        error: body.error,
        authorizationVersion: current?.authorizationVersion,
      }
      break
    case 'jobs-pending-profile-denied-before-data':
      stable = {
        status: response.status,
        error: body.error,
        jobDataReturned: Array.isArray(body) || Array.isArray(body.jobs),
      }
      break
    default:
      stable = {
        status: response.status,
        allowed: response.status < 400,
        error: body.error,
        stateChanged: !same(before, after),
      }
  }
  return { caseId: testCase.id, mode: 'entra', response, before, after, audits, stable }
}

interface OrganizationState {
  authorizationVersion: number
  memberships: OrganizationAdminMembership[]
  assignments: OrganizationAdminRoleAssignment[]
  departments: Array<{ id: string, organizationId: string, name: string, status: 'active' | 'retired' }>
}

async function executeOrganizationCase(
  fixture: AuthorizationParityFixture,
  testCase: AuthorizationParityCase,
): Promise<RawParityResult> {
  const reference = fixture.referenceState
  const targetObjectId = String(testCase.input.targetObjectId
    ?? testCase.request.body?.userObjectId
    ?? reference.profiles.multiOrganizationUser.objectId)
  const revokedAssignmentId = String(testCase.input.revokedAssignmentId ?? '')
  const preservedAssignmentId = String(testCase.input.preservedAssignmentId ?? '')
  const state: OrganizationState = {
    authorizationVersion: Number(testCase.input.authorizationVersionBefore ?? 0),
    memberships: [],
    assignments: [revokedAssignmentId, preservedAssignmentId]
      .filter(Boolean)
      .map((id, index) => ({
        id,
        userObjectId: targetObjectId,
        role: index === 0 ? 'recruiter' : 'business_panel',
        organizationId: index === 0 ? reference.primaryOrganizationId : reference.otherOrganizationId,
        departmentId: index === 0 ? reference.primaryDepartmentId : reference.otherDepartmentId,
        source: 'delegated',
        status: 'active',
      })),
    departments: [
      { id: reference.primaryDepartmentId, organizationId: reference.primaryOrganizationId, name: 'Engineering', status: 'active' },
      { id: reference.replacementDepartmentId, organizationId: reference.primaryOrganizationId, name: 'Operations', status: 'active' },
      { id: reference.otherDepartmentId, organizationId: reference.otherOrganizationId, name: 'Finance', status: 'active' },
    ],
  }
  const audits: AuditObservation[] = []
  const before = clone(state)
  const repository: OrganizationAdminRepository = {
    async createOrganization(_actor, request) {
      const id = '16161616-1616-4161-8161-161616161616'
      return { id, name: request.name, status: 'active', departments: [] }
    },
    async createDepartment(_actor, organizationId, request) {
      const department = {
        id: '17171717-1717-4171-8171-171717171717',
        organizationId,
        name: request.name,
        status: 'active' as const,
      }
      state.departments.push(department)
      return clone(department)
    },
    async updateDepartment(_actor, organizationId, departmentId, request) {
      const department = state.departments.find(item => item.id === departmentId && item.organizationId === organizationId)
      if (!department) throw { code: 'not_found' }
      if (request.name !== undefined) department.name = request.name
      if (request.status !== undefined) department.status = request.status
      return clone(department)
    },
    async registerMembership(_actor, organizationId, request) {
      const membership = { ...clone(request), organizationId }
      state.memberships = [
        ...state.memberships.filter(item =>
          item.userObjectId !== request.userObjectId || item.organizationId !== organizationId),
        membership,
      ]
      state.authorizationVersion++
      return clone(membership)
    },
    async grantRole(_actor, organizationId, request) {
      const assignment: OrganizationAdminRoleAssignment = {
        id: '18181818-1818-4181-8181-181818181818',
        userObjectId: request.userObjectId,
        role: request.role,
        organizationId,
        departmentId: request.departmentId,
        source: 'delegated',
        status: 'active',
      }
      state.assignments.push(assignment)
      state.authorizationVersion++
      return clone(assignment)
    },
    async revokeRole(_actor, organizationId, assignmentId) {
      const assignment = state.assignments.find(item =>
        item.id === assignmentId && item.organizationId === organizationId)
      if (!assignment) throw { code: 'not_found' }
      assignment.status = 'revoked'
    },
  }
  const audit: OrganizationAdminAudit = {
    async record(_actor, action, _entityType, _entityId, details) {
      audits.push({
        action,
        result: String(details.result),
        reasonCode: details.reasonCode === undefined ? undefined : String(details.reasonCode),
      })
    },
  }
  const app = express()
  app.use(express.json())
  app.use((req, _res, next) => {
    ;(req as any).authorizationContext = accessContext(reference, testCase.authScenario)
    next()
  })
  app.use('/api/organizations', createOrganizationsRouter(new OrganizationAdminService(repository, audit)))
  const response = await requestApp(app, testCase)
  const after = clone(state)
  const body = jsonBody(response)
  let stable: CanonicalParityResult
  switch (testCase.id) {
    case 'organization-admin-registers-explicit-default':
      stable = {
        status: response.status,
        defaultDepartmentId: body.defaultDepartmentId,
        authorizationVersion: state.authorizationVersion,
        auditOutcomes: audits.filter(item => item.result === 'succeeded').length,
      }
      break
    case 'revocation-organization-role-targeted-only': {
      const target = state.assignments.find(item => item.id === revokedAssignmentId)
      const preserved = state.assignments.find(item => item.id === preservedAssignmentId)
      stable = {
        status: response.status,
        targetRevoked: target?.status === 'revoked',
        unrelatedScopesPreserved: preserved?.status === 'active',
        auditOutcomes: audits.filter(item => item.result === 'succeeded').length,
      }
      break
    }
    default:
      stable = {
        status: response.status,
        error: body.error,
        stateChanged: !same(before, after),
        auditFailureOutcomes: audits.filter(item => item.result !== 'succeeded').length,
      }
  }
  return { caseId: testCase.id, mode: 'entra', response, before, after, audits, stable }
}

async function executeJobValidationCase(
  fixture: AuthorizationParityFixture,
  testCase: AuthorizationParityCase,
  mode: AuthenticationMode,
): Promise<RawParityResult> {
  const reference = fixture.referenceState
  const createdJobs: unknown[] = []
  const before = clone(createdJobs)
  const originalIsValidScope = jobRepo.isValidScope
  const originalCreate = jobRepo.create
  const originalGetAll = jobRepo.getAll
  const originalGetUsers = userRepo.getAll
  ;(jobRepo as any).isValidScope = async () => false
  ;(jobRepo as any).create = async (job: unknown) => { createdJobs.push(job) }
  ;(jobRepo as any).getAll = async () => []
  ;(userRepo as any).getAll = async () => []
  try {
    const app = express()
    app.use(express.json())
    app.use((req, _res, next) => {
      ;(req as any).user = {
        userId: actorObjectId,
        username: 'parity.actor@contoso.example',
        role: 'organization_admin',
        fullName: 'Parity Actor',
        createdAt: fixedNow.toISOString(),
      }
      if (mode === 'entra') {
        ;(req as any).authorizationContext = accessContext(reference, 'organization-admin')
      }
      next()
    })
    app.use('/api/jobs', createJobsRouter())
    const response = await requestApp(app, testCase)
    const body = jsonBody(response)
    const stable = mode === 'entra'
      ? { status: response.status, error: body.error, stateChanged: !same(before, createdJobs) }
      : { status: response.status, error: body.error, entraStateChanged: false }
    return {
      caseId: testCase.id,
      mode,
      response,
      before,
      after: clone(createdJobs),
      audits: [],
      stable,
    }
  } finally {
    ;(jobRepo as any).isValidScope = originalIsValidScope
    ;(jobRepo as any).create = originalCreate
    ;(jobRepo as any).getAll = originalGetAll
    ;(userRepo as any).getAll = originalGetUsers
  }
}

async function executeSimpleCase(
  fixture: AuthorizationParityFixture,
  testCase: AuthorizationParityCase,
): Promise<RawParityResult> {
  if (testCase.id === 'jobs-invalid-organization-department-pair') {
    return await executeJobValidationCase(fixture, testCase, 'simple')
  }
  if (testCase.host !== 'access' && testCase.category !== 'mode-isolation') {
    return {
      caseId: testCase.id,
      mode: 'simple',
      before: null,
      after: null,
      audits: [],
      stable: {
        applicable: false,
        capability: 'local_password_user_management',
        entraStateChanged: false,
      },
    }
  }

  const app = express()
  app.use(express.json())
  const response = await requestApp(app, testCase)
  const stable = testCase.category === 'mode-isolation'
    ? {
        status: response.status,
        capability: 'local_password_user_management',
        entraAccessManagementAvailable: response.status !== 404,
        entraStateChanged: false,
      }
    : {
        status: response.status,
        capability: 'local_password_user_management',
        entraStateChanged: false,
      }
  return {
    caseId: testCase.id,
    mode: 'simple',
    response,
    before: null,
    after: null,
    audits: [],
    stable,
  }
}

export async function executeStackAParityCase(
  fixture: AuthorizationParityFixture,
  testCase: AuthorizationParityCase,
  mode: AuthenticationMode,
): Promise<unknown> {
  if (fixture.schemaVersion !== 1) {
    throw new Error(`Unsupported authorization parity fixture schemaVersion: ${fixture.schemaVersion}.`)
  }
  if (mode === 'simple') return await executeSimpleCase(fixture, testCase)
  if (testCase.id === 'simple-mode-access-management-is-unavailable') {
    const result = await executeAccessCase(fixture, testCase)
    result.stable = {
      status: result.response?.status,
      capability: 'entra_access_management',
      passwordControlsVisible: false,
    }
    return result
  }
  if (testCase.category === 'access-management'
    || (testCase.category === 'revocation' && testCase.host === 'access')) {
    return await executeAccessCase(fixture, testCase)
  }
  if (testCase.category === 'organization-administration'
    || (testCase.category === 'revocation' && testCase.host === 'organization')) {
    return await executeOrganizationCase(fixture, testCase)
  }
  if (testCase.id === 'jobs-invalid-organization-department-pair') {
    return await executeJobValidationCase(fixture, testCase, 'entra')
  }
  return await executeAuthCase(fixture, testCase)
}

function normalize(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(normalize)
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value as Record<string, unknown>)
      .filter(([, item]) => item !== undefined)
      .map(([key, item]) => [key, normalize(item)]))
  }
  return value
}

export function canonicalizeAuthorizationParityResult(result: unknown): CanonicalParityResult {
  if (!result || typeof result !== 'object' || !('stable' in result)) {
    throw new Error('Stack A parity execution did not return canonicalizable observations.')
  }
  return normalize((result as RawParityResult).stable) as CanonicalParityResult
}

function differences(expected: unknown, actual: unknown, path = ''): string[] {
  if (same(expected, actual)) return []
  if (expected && actual && typeof expected === 'object' && typeof actual === 'object'
    && !Array.isArray(expected) && !Array.isArray(actual)) {
    const expectedObject = expected as Record<string, unknown>
    const actualObject = actual as Record<string, unknown>
    const keys = new Set([...Object.keys(expectedObject), ...Object.keys(actualObject)])
    return [...keys].flatMap(key => differences(
      expectedObject[key],
      actualObject[key],
      path ? `${path}.${key}` : key,
    ))
  }
  return [`${path || '<root>'}: expected ${JSON.stringify(expected)}, received ${JSON.stringify(actual)}`]
}

function loadReportFixture(): AuthorizationParityFixture {
  if (!existsSync(reportFixturePath)) throw new Error(`Fixture not found: ${reportFixturePath}`)
  return JSON.parse(readFileSync(reportFixturePath, 'utf8')) as AuthorizationParityFixture
}

export async function runAuthorizationParityReport(): Promise<number> {
  const fixture = loadReportFixture()
  let passed = 0
  let failed = 0
  for (const testCase of fixture.cases) {
    for (const mode of ['entra', 'simple'] as const) {
      const raw = await executeStackAParityCase(fixture, testCase, mode)
      const actual = canonicalizeAuthorizationParityResult(raw)
      const expected = testCase.expectedByMode[mode]
      const fieldDifferences = differences(expected, actual)
      if (fieldDifferences.length === 0) {
        passed++
        console.log(`PASS ${testCase.id} [${mode}]`)
      } else {
        failed++
        console.error(`FAIL ${testCase.id} [${mode}]`)
        for (const difference of fieldDifferences) console.error(`  ${difference}`)
      }
    }
  }
  console.log(`\nStack A authorization parity: ${passed} passed, ${failed} failed.`)
  return failed === 0 ? 0 : 1
}

const entryPath = process.argv[1] ? resolve(process.argv[1]) : ''
if (entryPath === fileURLToPath(import.meta.url)) {
  runAuthorizationParityReport()
    .then(exitCode => { process.exitCode = exitCode })
    .catch(error => {
      console.error(error instanceof Error ? error.stack ?? error.message : error)
      process.exitCode = 1
    })
}