export type OrganizationStatus = 'active' | 'retired'
export type DelegatedOrganizationRole = 'organization_admin' | 'recruiter' | 'business_panel'

export interface OrganizationAdminActor {
  tenantId: string
  objectId: string
  globalAdmin: boolean
  organizationAdminIds: string[]
  correlationId: string
}

export interface OrganizationAdminDepartment {
  id: string
  organizationId: string
  name: string
  status: OrganizationStatus
}

export interface OrganizationAdminOrganization {
  id: string
  name: string
  status: OrganizationStatus
  departments: OrganizationAdminDepartment[]
}

export interface OrganizationAdminMembership {
  userObjectId: string
  organizationId: string
  departmentIds: string[]
  defaultDepartmentId: string
}

export interface OrganizationAdminRoleAssignment {
  id: string
  userObjectId: string
  role: DelegatedOrganizationRole
  organizationId: string
  departmentId: string | null
  source: 'delegated'
  status: 'active' | 'revoked'
}

export interface CreateOrganizationRequest {
  name: string
  initialDepartmentName: string
}

export interface CreateDepartmentRequest {
  name: string
}

export interface UpdateDepartmentRequest {
  name?: string
  status?: OrganizationStatus
}

export interface RegisterOrganizationMembershipRequest {
  userObjectId: string
  departmentIds: string[]
  defaultDepartmentId: string
}

export interface GrantOrganizationRoleRequest {
  userObjectId: string
  role: DelegatedOrganizationRole
  departmentId: string | null
}

export interface OrganizationAdminRepository {
  createOrganization(actor: OrganizationAdminActor, request: CreateOrganizationRequest): Promise<OrganizationAdminOrganization>
  createDepartment(actor: OrganizationAdminActor, organizationId: string, request: CreateDepartmentRequest): Promise<OrganizationAdminDepartment>
  updateDepartment(actor: OrganizationAdminActor, organizationId: string, departmentId: string, request: UpdateDepartmentRequest): Promise<OrganizationAdminDepartment>
  registerMembership(actor: OrganizationAdminActor, organizationId: string, request: RegisterOrganizationMembershipRequest): Promise<OrganizationAdminMembership>
  grantRole(actor: OrganizationAdminActor, organizationId: string, request: GrantOrganizationRoleRequest): Promise<OrganizationAdminRoleAssignment>
  revokeRole(actor: OrganizationAdminActor, organizationId: string, assignmentId: string): Promise<void>
}

export interface OrganizationAdminAudit {
  record(
    actor: OrganizationAdminActor,
    action: string,
    entityType: string,
    entityId: string,
    details: Record<string, unknown>,
  ): Promise<void>
}

type OrganizationAdminErrorCode = 'invalid_scope' | 'forbidden' | 'not_found' | 'conflict'

export class OrganizationAdminError extends Error {
  constructor(
    readonly code: OrganizationAdminErrorCode,
    message: string,
    readonly statusCode: 400 | 403 | 404 | 409,
  ) {
    super(message)
  }
}

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

export class OrganizationAdminService {
  constructor(
    readonly repository: OrganizationAdminRepository,
    private readonly audit?: OrganizationAdminAudit,
  ) {}

  async createOrganization(actor: OrganizationAdminActor, request: CreateOrganizationRequest): Promise<OrganizationAdminOrganization> {
    return await this.execute(actor, 'organization.created', 'Organization', '', { name: request?.name }, async () => {
      this.requireGlobalAdmin(actor)
      this.requireName(request?.name, 200, 'Organization name')
      this.requireName(request?.initialDepartmentName, 100, 'Initial department name')
      const result = await this.repository.createOrganization(actor, {
        name: request.name.trim(),
        initialDepartmentName: request.initialDepartmentName.trim(),
      })
      return { result, entityId: result.id }
    })
  }

  async createDepartment(actor: OrganizationAdminActor, organizationId: string, request: CreateDepartmentRequest): Promise<OrganizationAdminDepartment> {
    return await this.execute(actor, 'department.created', 'Department', organizationId, { organizationId }, async () => {
      this.requireOrganizationAuthority(actor, organizationId)
      this.requireName(request?.name, 100, 'Department name')
      const result = await this.repository.createDepartment(actor, organizationId, { name: request.name.trim() })
      this.requireMatchingOrganization(result.organizationId, organizationId)
      return { result, entityId: result.id }
    })
  }

  async updateDepartment(
    actor: OrganizationAdminActor,
    organizationId: string,
    departmentId: string,
    request: UpdateDepartmentRequest,
  ): Promise<OrganizationAdminDepartment> {
    return await this.execute(actor, 'department.updated', 'Department', departmentId, {
      organizationId,
      requestedStatus: request?.status,
    }, async () => {
      this.requireOrganizationAuthority(actor, organizationId)
      this.requireUuid(departmentId, 'Department ID')
      if (!request || (request.name === undefined && request.status === undefined)) {
        throw new OrganizationAdminError('invalid_scope', 'At least one department change is required.', 400)
      }
      if (request.name !== undefined) this.requireName(request.name, 100, 'Department name')
      if (request.status !== undefined && request.status !== 'active' && request.status !== 'retired') {
        throw new OrganizationAdminError('invalid_scope', 'Department status is invalid.', 400)
      }
      const normalizedRequest = {
        ...(request.name === undefined ? {} : { name: request.name.trim() }),
        ...(request.status === undefined ? {} : { status: request.status }),
      }
      const result = await this.repository.updateDepartment(actor, organizationId, departmentId, normalizedRequest)
      this.requireMatchingOrganization(result.organizationId, organizationId)
      return { result, entityId: result.id }
    })
  }

  async registerMembership(
    actor: OrganizationAdminActor,
    organizationId: string,
    request: RegisterOrganizationMembershipRequest,
  ): Promise<OrganizationAdminMembership> {
    return await this.execute(actor, 'organization.membership.updated', 'OrganizationMembership', request?.userObjectId ?? '', {
      organizationId,
      subjectObjectId: request?.userObjectId,
      defaultDepartmentId: request?.defaultDepartmentId,
    }, async () => {
      this.requireOrganizationAuthority(actor, organizationId)
      this.requireUuid(request?.userObjectId, 'User object ID')
      if (!Array.isArray(request?.departmentIds) || request.departmentIds.length === 0) {
        throw new OrganizationAdminError('invalid_scope', 'At least one department membership is required.', 400)
      }
      if (new Set(request.departmentIds).size !== request.departmentIds.length
        || request.departmentIds.some(departmentId => !uuidPattern.test(departmentId))) {
        throw new OrganizationAdminError('invalid_scope', 'Department memberships must be unique valid IDs.', 400)
      }
      if (!request.defaultDepartmentId || !request.departmentIds.includes(request.defaultDepartmentId)) {
        throw new OrganizationAdminError('invalid_scope', 'The explicit default must be an active requested department membership.', 400)
      }
      const result = await this.repository.registerMembership(actor, organizationId, request)
      this.requireMatchingOrganization(result.organizationId, organizationId)
      if (!result.departmentIds.includes(result.defaultDepartmentId)) {
        throw new OrganizationAdminError('conflict', 'The membership result does not contain its explicit default.', 409)
      }
      return { result, entityId: request.userObjectId }
    })
  }

  async grantRole(
    actor: OrganizationAdminActor,
    organizationId: string,
    request: GrantOrganizationRoleRequest,
  ): Promise<OrganizationAdminRoleAssignment> {
    return await this.execute(actor, 'organization.role.granted', 'RoleAssignment', request?.userObjectId ?? '', {
      organizationId,
      subjectObjectId: request?.userObjectId,
      role: request?.role,
      departmentId: request?.departmentId,
    }, async () => {
      this.requireOrganizationAuthority(actor, organizationId)
      this.requireUuid(request?.userObjectId, 'User object ID')
      if (!request || !['organization_admin', 'recruiter', 'business_panel'].includes(request.role)) {
        throw new OrganizationAdminError('invalid_scope', 'Application-wide Admin cannot be delegated through an organization.', 400)
      }
      if (request.role === 'organization_admin' && !actor.globalAdmin) {
        throw new OrganizationAdminError('forbidden', 'Only an application Admin may delegate Organization Admin.', 403)
      }
      if (request.role === 'organization_admin' && request.departmentId !== null) {
        throw new OrganizationAdminError('invalid_scope', 'Organization Admin cannot have department scope.', 400)
      }
      if (request.role === 'recruiter' && !request.departmentId) {
        throw new OrganizationAdminError('invalid_scope', 'Recruiter requires department scope.', 400)
      }
      if (request.departmentId !== null) this.requireUuid(request.departmentId, 'Department ID')
      const result = await this.repository.grantRole(actor, organizationId, request)
      this.requireMatchingOrganization(result.organizationId, organizationId)
      if (result.source !== 'delegated') {
        throw new OrganizationAdminError('conflict', 'Only delegated assignments can be managed here.', 409)
      }
      return { result, entityId: result.id }
    })
  }

  async revokeRole(actor: OrganizationAdminActor, organizationId: string, assignmentId: string): Promise<void> {
    await this.execute(actor, 'organization.role.revoked', 'RoleAssignment', assignmentId, { organizationId }, async () => {
      this.requireOrganizationAuthority(actor, organizationId)
      this.requireUuid(assignmentId, 'Assignment ID')
      await this.repository.revokeRole(actor, organizationId, assignmentId)
      return { result: undefined, entityId: assignmentId }
    })
  }

  private async execute<T>(
    actor: OrganizationAdminActor,
    action: string,
    entityType: string,
    initialEntityId: string,
    details: Record<string, unknown>,
    operation: () => Promise<{ result: T, entityId: string }>,
  ): Promise<T> {
    try {
      const { result, entityId } = await operation()
      if (this.audit) {
        await this.audit.record(actor, action, entityType, entityId || initialEntityId, {
          ...details,
          result: 'succeeded',
        })
      }
      return result
    } catch (error) {
      const mapped = this.mapRepositoryError(error)
      try {
        await this.audit?.record(actor, action, entityType, initialEntityId, {
          ...details,
          result: mapped.code === 'forbidden' || mapped.code === 'invalid_scope' ? 'denied' : 'failed',
          reasonCode: mapped.code,
        })
      } catch {
        // The original canonical operation error remains authoritative.
      }
      throw mapped
    }
  }

  private mapRepositoryError(error: unknown): OrganizationAdminError {
    if (error instanceof OrganizationAdminError) return error
    const value = error as { code?: string, message?: string }
    if (value?.code === 'not_found') return new OrganizationAdminError('not_found', value.message ?? 'The requested resource was not found.', 404)
    if (value?.code === 'conflict' || value?.code === 'SQLITE_CONSTRAINT' || value?.code === 'EREQUEST') {
      return new OrganizationAdminError('conflict', value.message ?? 'The requested change conflicts with current organization state.', 409)
    }
    return new OrganizationAdminError('conflict', 'The organization change could not be completed.', 409)
  }

  private requireGlobalAdmin(actor: OrganizationAdminActor): void {
    if (!actor.globalAdmin) throw new OrganizationAdminError('forbidden', 'Application Admin access is required.', 403)
  }

  private requireOrganizationAuthority(actor: OrganizationAdminActor, organizationId: string): void {
    this.requireUuid(organizationId, 'Organization ID')
    if (!actor.globalAdmin && !actor.organizationAdminIds.includes(organizationId)) {
      throw new OrganizationAdminError('forbidden', 'This organization is outside the actor scope.', 403)
    }
  }

  private requireMatchingOrganization(actualOrganizationId: string, expectedOrganizationId: string): void {
    if (actualOrganizationId !== expectedOrganizationId) {
      throw new OrganizationAdminError('conflict', 'The persisted resource belongs to another organization.', 409)
    }
  }

  private requireName(value: string | undefined, maxLength: number, label: string): void {
    if (!value?.trim() || value.trim().length > maxLength) {
      throw new OrganizationAdminError('invalid_scope', `${label} is required and must not exceed ${maxLength} characters.`, 400)
    }
  }

  private requireUuid(value: string | undefined, label: string): void {
    if (!value || !uuidPattern.test(value)) {
      throw new OrganizationAdminError('invalid_scope', `${label} must be a valid UUID.`, 400)
    }
  }
}