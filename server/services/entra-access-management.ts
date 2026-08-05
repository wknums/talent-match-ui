import type {
  CanonicalApiError,
  EntraAccessUser,
  EntraAccessUserPage,
  PutOrganizationAccessRequest,
  UpdateEntraAccessUserRequest,
} from '../../src/types/index.js'
import type { EntraAccessManagementRepository } from '../storage/repos/access-management-repo.js'

export interface AccessManagementActor {
  tenantId: string
  objectId: string
  globalAdmin: boolean
  organizationAdminIds: string[]
  correlationId: string
}

export interface EntraAccessSearchOptions {
  search?: string
  organizationId?: string
  status?: 'pending' | 'active' | 'disabled'
  cursor?: string
  limit: number
}

type AccessErrorCode = Extract<CanonicalApiError['error'], 'invalid_scope' | 'forbidden' | 'not_found' | 'version_conflict'>

export class EntraAccessError extends Error {
  constructor(
    readonly code: AccessErrorCode,
    message: string,
    readonly statusCode: number,
  ) {
    super(message)
  }
}

export class EntraAccessManagementService {
  constructor(readonly repository: EntraAccessManagementRepository) {}

  async list(actor: AccessManagementActor, options: EntraAccessSearchOptions): Promise<EntraAccessUserPage> {
    if (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 100) {
      throw new EntraAccessError('invalid_scope', 'Limit must be between 1 and 100.', 400)
    }
    if (options.search && options.search.length > 200) {
      throw new EntraAccessError('invalid_scope', 'Search text is too long.', 400)
    }
    if (options.organizationId) this.requireOrganizationAuthority(actor, options.organizationId)
    return await this.repository.list(actor, options)
  }

  async get(actor: AccessManagementActor, objectId: string): Promise<EntraAccessUser> {
    this.requireObjectId(objectId)
    const result = await this.repository.get(actor, objectId)
    if (!result) throw new EntraAccessError('not_found', 'The Entra access profile was not found.', 404)
    return result
  }

  async updateUser(
    actor: AccessManagementActor,
    objectId: string,
    request: UpdateEntraAccessUserRequest,
  ): Promise<EntraAccessUser> {
    this.requireGlobalAdmin(actor)
    this.requireObjectId(objectId)
    this.requireVersion(request.expectedVersion)
    if (request.profile === undefined && request.isActive === undefined) {
      throw new EntraAccessError('invalid_scope', 'At least one profile change is required.', 400)
    }
    if (request.profile) this.validateProfile(request.profile)
    return await this.repository.updateUser(actor, objectId, request)
  }

  async putOrganizationAccess(
    actor: AccessManagementActor,
    objectId: string,
    organizationId: string,
    request: PutOrganizationAccessRequest,
  ): Promise<EntraAccessUser> {
    this.requireObjectId(objectId)
    this.requireObjectId(organizationId)
    this.requireOrganizationAuthority(actor, organizationId)
    this.requireVersion(request.expectedVersion)
    this.validateProfile(request.profile)
    this.validateMembership(request)

    const duplicateRoles = new Set<string>()
    for (const assignment of request.roleAssignments) {
      if (assignment.role === 'organization_admin' && !actor.globalAdmin) {
        throw new EntraAccessError('forbidden', 'Organization Admin access cannot be delegated by this actor.', 403)
      }
      const key = `${assignment.role}:${assignment.departmentId ?? ''}`
      if (duplicateRoles.has(key)) {
        throw new EntraAccessError('invalid_scope', 'Role assignments must be unique.', 400)
      }
      duplicateRoles.add(key)
      if (assignment.role === 'recruiter' && !assignment.departmentId) {
        throw new EntraAccessError('invalid_scope', 'Recruiter access requires a department.', 400)
      }
      if (assignment.role === 'organization_admin' && assignment.departmentId) {
        throw new EntraAccessError('invalid_scope', 'Organization Admin access cannot have a department.', 400)
      }
      if (assignment.departmentId && !request.membership.departmentIds.includes(assignment.departmentId)) {
        throw new EntraAccessError('invalid_scope', 'Role departments must be active memberships.', 400)
      }
    }
    if (request.membership.status === 'revoked' && request.roleAssignments.length > 0) {
      throw new EntraAccessError('invalid_scope', 'Revoked membership cannot retain delegated roles.', 400)
    }
    return await this.repository.putOrganizationAccess(actor, objectId, organizationId, request)
  }

  async revokeRole(
    actor: AccessManagementActor,
    objectId: string,
    organizationId: string,
    assignmentId: string,
    expectedVersion: number,
  ): Promise<EntraAccessUser> {
    this.requireObjectId(objectId)
    this.requireObjectId(organizationId)
    this.requireOrganizationAuthority(actor, organizationId)
    this.requireVersion(expectedVersion)
    if (!assignmentId.trim()) throw new EntraAccessError('invalid_scope', 'Assignment ID is required.', 400)
    return await this.repository.revokeRole(actor, objectId, organizationId, assignmentId, expectedVersion)
  }

  private requireGlobalAdmin(actor: AccessManagementActor): void {
    if (!actor.globalAdmin) throw new EntraAccessError('forbidden', 'Application Admin access is required.', 403)
  }

  private requireOrganizationAuthority(actor: AccessManagementActor, organizationId: string): void {
    if (!actor.globalAdmin && !actor.organizationAdminIds.includes(organizationId)) {
      throw new EntraAccessError('forbidden', 'This organization is outside the actor scope.', 403)
    }
  }

  private requireVersion(version: number): void {
    if (!Number.isInteger(version) || version < 0) {
      throw new EntraAccessError('invalid_scope', 'A valid expectedVersion is required.', 400)
    }
  }

  private requireObjectId(value: string): void {
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value)) {
      throw new EntraAccessError('invalid_scope', 'A valid immutable object ID is required.', 400)
    }
  }

  private validateProfile(profile: PutOrganizationAccessRequest['profile']): void {
    if (!profile.username.trim() || profile.username.length > 100
      || !profile.fullName.trim() || profile.fullName.length > 200
      || (profile.email !== null && profile.email.length > 320)) {
      throw new EntraAccessError('invalid_scope', 'Profile fields are invalid.', 400)
    }
    if (profile.email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(profile.email)) {
      throw new EntraAccessError('invalid_scope', 'Email must be valid.', 400)
    }
  }

  private validateMembership(request: PutOrganizationAccessRequest): void {
    const { membership } = request
    if (new Set(membership.departmentIds).size !== membership.departmentIds.length) {
      throw new EntraAccessError('invalid_scope', 'Department memberships must be unique.', 400)
    }
    if (membership.status === 'active') {
      if (membership.departmentIds.length === 0
        || !membership.defaultDepartmentId
        || !membership.departmentIds.includes(membership.defaultDepartmentId)) {
        throw new EntraAccessError('invalid_scope', 'Active access requires an explicit default from its departments.', 400)
      }
      return
    }
    if (membership.departmentIds.length > 0 || membership.defaultDepartmentId !== null) {
      throw new EntraAccessError('invalid_scope', 'Revoked access cannot retain active departments or a default.', 400)
    }
  }
}