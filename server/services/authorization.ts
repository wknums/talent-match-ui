import type {
  AuthErrorCode,
  RoleAssignment,
  RoleAssignmentSource,
  RoleGroupMapping,
  UserRole,
} from '../../src/types/index.js'
import type { NormalizedEntraClaims } from './entra-token.js'
import type { StoredUser } from '../storage/repos/user-repo.js'
import { getAuthorizationErrorMessage } from './authorization-errors.js'

export { getAuthorizationErrorMessage } from './authorization-errors.js'

const roleLabels: Record<UserRole, string> = {
  admin: 'Admin',
  organization_admin: 'Organization Admin',
  recruiter: 'Recruiter',
  business_panel: 'Analytics Viewer',
}

const roleRank: Record<UserRole, number> = {
  admin: 4,
  organization_admin: 3,
  recruiter: 2,
  business_panel: 1,
}

export interface AuthorizationDepartmentMembership {
  departmentId: string
  departmentName: string
}

export interface AuthorizationMembership {
  organizationId: string
  organizationName: string
  defaultDepartmentId: string | null
  departments: AuthorizationDepartmentMembership[]
}

export interface ServerAuthorization {
  role: UserRole
  roleLabel: string
  organizationId: string | null
  departmentId: string | null
  assignmentSource: RoleAssignmentSource
}

export interface ServerAuthorizationContext {
  userId: string
  tenantId: string
  objectId: string
  username: string
  fullName: string
  email: string | null
  globalRole: 'admin' | null
  authorizationVersion: number
  memberships: AuthorizationMembership[]
  authorizations: ServerAuthorization[]
  tokenIssuedAt: string
  refreshRequiredAt: string
}

type EntraProfile = Required<Pick<StoredUser, 'userId' | 'username' | 'fullName' | 'createdAt'>>
  & Pick<StoredUser, 'email' | 'role' | 'entraTenantId' | 'entraObjectId'>

export interface AuthorizationRepositories {
  users: {
    getByEntraIdentity(tenantId: string, objectId: string): Promise<StoredUser | undefined>
    upsertEntraProfile(profile: EntraProfile): Promise<StoredUser>
    update(userId: string, fields: { lastLogin?: string }): Promise<void>
  }
  organizations: {
    getAuthorizationMemberships(userId: string): Promise<AuthorizationMembership[]>
  }
  roleAssignments: {
    getActiveForIdentity(tenantId: string, objectId: string): Promise<RoleAssignment[]>
    getGroupMappingsByIds(mappingIds: string[]): Promise<RoleGroupMapping[]>
  }
}

export interface AuthorizationResolverOptions {
  bootstrapObjectId?: string
  now?: () => Date
}

export class AuthorizationError extends Error {
  readonly code: AuthErrorCode
  readonly statusCode: 403
  readonly pendingProfileCreated: boolean

  constructor(code: AuthErrorCode, pendingProfileCreated = false) {
    super(getAuthorizationErrorMessage(code))
    this.name = 'AuthorizationError'
    this.code = code
    this.statusCode = 403
    this.pendingProfileCreated = pendingProfileCreated
  }
}

function requireMembership(
  memberships: AuthorizationMembership[],
  role: UserRole,
  organizationId: string | undefined,
  departmentId: string | undefined,
) {
  if (role === 'admin') {
    if (organizationId || departmentId) throw new AuthorizationError('scope_unmapped')
    return
  }
  if (!organizationId) throw new AuthorizationError('scope_unmapped')
  const organization = memberships.find(item => item.organizationId === organizationId)
  if (!organization) {
    throw new AuthorizationError('membership_missing')
  }
  if (role === 'organization_admin' && departmentId) throw new AuthorizationError('scope_unmapped')
  if (role === 'recruiter' && !departmentId) throw new AuthorizationError('scope_unmapped')
  if (departmentId && !organization.departments.some(item => item.departmentId === departmentId)) {
    throw new AuthorizationError('membership_missing')
  }
}

function validateMembershipDefaults(memberships: AuthorizationMembership[]) {
  if (memberships.some(membership =>
    !membership.defaultDepartmentId
    || !membership.departments.some(department => department.departmentId === membership.defaultDepartmentId))) {
    throw new AuthorizationError('membership_missing')
  }
}

function validateAssignmentIdentity(
  assignment: RoleAssignment,
  user: StoredUser,
  claims: NormalizedEntraClaims,
) {
  if (
    assignment.status !== 'active'
    || assignment.userId !== user.userId
    || assignment.tenantId !== claims.tenantId
    || assignment.userObjectId !== claims.objectId
  ) {
    throw new AuthorizationError('assignment_revoked')
  }
}

function validateGroupAssignment(
  assignment: RoleAssignment,
  mapping: RoleGroupMapping | undefined,
  claims: NormalizedEntraClaims,
) {
  if (!assignment.roleGroupMappingId || !mapping || !mapping.enabled) {
    throw new AuthorizationError('assignment_revoked')
  }
  if (
    mapping.tenantId !== claims.tenantId
    || mapping.role !== assignment.role
    || mapping.organizationId !== assignment.organizationId
    || mapping.departmentId !== assignment.departmentId
  ) {
    throw new AuthorizationError('scope_unmapped')
  }
  if (!claims.roles.includes(assignment.role)) throw new AuthorizationError('role_missing')
  if (!claims.groups.includes(mapping.groupObjectId)) throw new AuthorizationError('assignment_missing')
}

function validateDelegatedAssignment(assignment: RoleAssignment) {
  if (assignment.role === 'admin' || assignment.roleGroupMappingId) {
    throw new AuthorizationError('assignment_revoked')
  }
}

function validateBootstrapAssignment(
  assignment: RoleAssignment,
  claims: NormalizedEntraClaims,
  bootstrapObjectId: string | undefined,
) {
  if (
    assignment.role !== 'admin'
    || assignment.roleGroupMappingId
    || !bootstrapObjectId
    || claims.objectId !== bootstrapObjectId.toLowerCase()
  ) {
    throw new AuthorizationError('assignment_revoked')
  }
  if (!claims.roles.includes('admin')) throw new AuthorizationError('role_missing')
}

function effectiveAssignments(assignments: RoleAssignment[]): RoleAssignment[] {
  const byScope = new Map<string, RoleAssignment>()
  for (const assignment of assignments) {
    const scope = `${assignment.organizationId ?? 'global'}:${assignment.departmentId ?? 'global'}`
    const current = byScope.get(scope)
    if (!current || roleRank[assignment.role] > roleRank[current.role]) {
      byScope.set(scope, assignment)
    }
  }
  return [...byScope.values()]
}

export function createAuthorizationResolver(
  repositories: AuthorizationRepositories,
  options: AuthorizationResolverOptions = {},
) {
  const now = options.now ?? (() => new Date())

  return {
    async resolve(claims: NormalizedEntraClaims): Promise<ServerAuthorizationContext> {
      const existing = await repositories.users.getByEntraIdentity(claims.tenantId, claims.objectId)
      const user = await repositories.users.upsertEntraProfile({
        userId: existing?.userId ?? claims.objectId,
        username: claims.username,
        fullName: claims.fullName,
        email: claims.email,
        role: existing?.role ?? 'recruiter',
        entraTenantId: claims.tenantId,
        entraObjectId: claims.objectId,
        createdAt: existing?.createdAt ?? now().toISOString(),
      })

      if (user.isActive === false) throw new AuthorizationError('identity_disabled')

      const assignments = await repositories.roleAssignments.getActiveForIdentity(
        claims.tenantId,
        claims.objectId,
      )
      if (assignments.length === 0) {
        throw new AuthorizationError('assignment_missing', existing === undefined)
      }

      const memberships = await repositories.organizations.getAuthorizationMemberships(user.userId)
      if (memberships.length === 0 || memberships.every(item => item.departments.length === 0)) {
        throw new AuthorizationError('membership_missing')
      }
      validateMembershipDefaults(memberships)
      const mappingIds = assignments
        .filter(item => item.source === 'group' && item.roleGroupMappingId)
        .map(item => item.roleGroupMappingId!)
      const mappings = await repositories.roleAssignments.getGroupMappingsByIds(mappingIds)
      const mappingsById = new Map(mappings.map(mapping => [mapping.mappingId, mapping]))

      for (const assignment of assignments) {
        validateAssignmentIdentity(assignment, user, claims)
        requireMembership(
          memberships,
          assignment.role,
          assignment.organizationId,
          assignment.departmentId,
        )
        switch (assignment.source) {
          case 'group':
            validateGroupAssignment(
              assignment,
              assignment.roleGroupMappingId
                ? mappingsById.get(assignment.roleGroupMappingId)
                : undefined,
              claims,
            )
            break
          case 'delegated':
            validateDelegatedAssignment(assignment)
            break
          case 'bootstrap':
            validateBootstrapAssignment(assignment, claims, options.bootstrapObjectId)
            break
          default:
            throw new AuthorizationError('assignment_revoked')
        }
      }

      const authorizations = effectiveAssignments(assignments).map<ServerAuthorization>(assignment => ({
        role: assignment.role,
        roleLabel: roleLabels[assignment.role],
        organizationId: assignment.organizationId ?? null,
        departmentId: assignment.departmentId ?? null,
        assignmentSource: assignment.source,
      }))
      const tokenIssuedAt = claims.issuedAt.toISOString()

      return {
        userId: user.userId,
        tenantId: claims.tenantId,
        objectId: claims.objectId,
        username: user.username,
        fullName: user.fullName,
        email: user.email ?? null,
        globalRole: authorizations.some(item => item.role === 'admin') ? 'admin' : null,
        authorizationVersion: user.authorizationVersion ?? 0,
        memberships,
        authorizations,
        tokenIssuedAt,
        refreshRequiredAt: new Date(claims.issuedAt.getTime() + 15 * 60 * 1000).toISOString(),
      }
    },
  }
}