import { randomUUID } from 'node:crypto'
import { getPool, isAzureSql, sql } from '../db.js'
import { T } from '../table-names.js'
import type { Department, DepartmentMembership, Organization, OrganizationMembership } from '../../../src/types/index.js'
import type { AuthorizationMembership } from '../../services/authorization.js'
import {
  OrganizationAdminError,
  type CreateDepartmentRequest,
  type CreateOrganizationRequest,
  type GrantOrganizationRoleRequest,
  type OrganizationAdminActor,
  type OrganizationAdminDepartment,
  type OrganizationAdminMembership,
  type OrganizationAdminOrganization,
  type OrganizationAdminRoleAssignment,
  type RegisterOrganizationMembershipRequest,
  type UpdateDepartmentRequest,
} from '../../services/organization-admin.js'
import type { StorageExecutor } from '../types.js'

const asIso = (value: any) => value?.toISOString?.() ?? value

interface StoredOrganizationAuthority {
  globalAdmin: boolean
  organizationAdminIds: string[]
}

function lockTable(tableName: string): string {
  const table = T(tableName)
  return isAzureSql ? `${table} WITH (UPDLOCK, HOLDLOCK)` : table
}

async function readOrganizationAuthority(
  executor: StorageExecutor,
  actor: OrganizationAdminActor,
): Promise<StoredOrganizationAuthority> {
  const user = await executor.request()
    .input('tenantId', sql.NVarChar, actor.tenantId)
    .input('objectId', sql.NVarChar, actor.objectId)
    .query(`SELECT Id, IsActive FROM ${lockTable('Users')}
      WHERE AuthenticationProvider = 'entra' AND EntraTenantId = @tenantId AND EntraObjectId = @objectId`)
  if (!user.recordset[0] || !(user.recordset[0].IsActive === true || user.recordset[0].IsActive === 1)) {
    throw new OrganizationAdminError('forbidden', 'The actor is not an active Entra identity.', 403)
  }
  const assignments = await executor.request()
    .input('tenantId', sql.NVarChar, actor.tenantId)
    .input('objectId', sql.NVarChar, actor.objectId)
    .input('userId', sql.NVarChar, user.recordset[0].Id)
    .query(`SELECT ra.Role, ra.OrganizationId FROM ${lockTable('RoleAssignments')} ra
      WHERE ra.TenantId = @tenantId AND ra.UserObjectId = @objectId AND ra.Status = 'active'
        AND (ra.Role = 'admin' OR (ra.Role = 'organization_admin' AND EXISTS (
          SELECT 1 FROM ${T('OrganizationMemberships')} om
          WHERE om.UserId = @userId AND om.OrganizationId = ra.OrganizationId AND om.Status = 'active'
        )))`)
  return {
    globalAdmin: assignments.recordset.some(row => row.Role === 'admin'),
    organizationAdminIds: [...new Set(assignments.recordset
      .filter(row => row.Role === 'organization_admin' && row.OrganizationId)
      .map(row => String(row.OrganizationId)))],
  }
}

function requireStoredAuthority(authority: StoredOrganizationAuthority, organizationId?: string): void {
  if (authority.globalAdmin) return
  if (organizationId && authority.organizationAdminIds.includes(organizationId)) return
  throw new OrganizationAdminError('forbidden', 'The actor does not have authority for this organization.', 403)
}

async function organizationMutation<T>(work: (transaction: StorageExecutor) => Promise<T>): Promise<T> {
  const pool = await getPool()
  const transaction = pool.transaction()
  await transaction.begin(isAzureSql ? sql.ISOLATION_LEVEL.SERIALIZABLE : undefined)
  try {
    const result = await work(transaction)
    await transaction.commit()
    return result
  } catch (error) {
    await transaction.rollback()
    throw error
  }
}

async function readTargetUser(executor: StorageExecutor, actor: OrganizationAdminActor, objectId: string): Promise<any> {
  const result = await executor.request()
    .input('tenantId', sql.NVarChar, actor.tenantId)
    .input('objectId', sql.NVarChar, objectId)
    .query(`SELECT Id, EntraObjectId, IsActive FROM ${lockTable('Users')}
      WHERE AuthenticationProvider = 'entra' AND EntraTenantId = @tenantId AND EntraObjectId = @objectId`)
  if (!result.recordset[0]) {
    throw new OrganizationAdminError('not_found', 'The tenant-verified Entra profile was not found.', 404)
  }
  if (!(result.recordset[0].IsActive === true || result.recordset[0].IsActive === 1)) {
    throw new OrganizationAdminError('conflict', 'A disabled identity cannot receive active organization access.', 409)
  }
  return result.recordset[0]
}

async function advanceAuthorizationVersion(executor: StorageExecutor, userId: string): Promise<void> {
  await executor.request().input('userId', sql.NVarChar, userId)
    .query(`UPDATE ${T('Users')} SET AuthorizationVersion = AuthorizationVersion + 1 WHERE Id = @userId`)
}

export const organizationRepo = {
  async createOrganization(
    actor: OrganizationAdminActor,
    request: CreateOrganizationRequest,
  ): Promise<OrganizationAdminOrganization> {
    return await organizationMutation(async transaction => {
      const authority = await readOrganizationAuthority(transaction, actor)
      requireStoredAuthority(authority)
      const organizationId = randomUUID()
      const departmentId = randomUUID()
      await transaction.request()
        .input('id', sql.NVarChar, organizationId)
        .input('name', sql.NVarChar, request.name)
        .input('updatedBy', sql.NVarChar, actor.objectId)
        .query(`INSERT INTO ${T('Organizations')} (Id, Name, Status, CreatedAt, UpdatedAt, UpdatedBy)
          VALUES (@id, @name, 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
      await transaction.request()
        .input('id', sql.NVarChar, departmentId)
        .input('organizationId', sql.NVarChar, organizationId)
        .input('name', sql.NVarChar, request.initialDepartmentName)
        .input('updatedBy', sql.NVarChar, actor.objectId)
        .query(`INSERT INTO ${T('Departments')} (Id, OrganizationId, Name, Status, CreatedAt, UpdatedAt, UpdatedBy)
          VALUES (@id, @organizationId, @name, 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
      return {
        id: organizationId,
        name: request.name,
        status: 'active',
        departments: [{ id: departmentId, organizationId, name: request.initialDepartmentName, status: 'active' }],
      }
    })
  },

  async createDepartment(
    actor: OrganizationAdminActor,
    organizationId: string,
    request: CreateDepartmentRequest,
  ): Promise<OrganizationAdminDepartment> {
    return await organizationMutation(async transaction => {
      requireStoredAuthority(await readOrganizationAuthority(transaction, actor), organizationId)
      const organization = await transaction.request().input('organizationId', sql.NVarChar, organizationId)
        .query(`SELECT Id FROM ${lockTable('Organizations')} WHERE Id = @organizationId AND Status = 'active'`)
      if (!organization.recordset[0]) throw new OrganizationAdminError('not_found', 'The active organization was not found.', 404)
      const id = randomUUID()
      await transaction.request().input('id', sql.NVarChar, id).input('organizationId', sql.NVarChar, organizationId)
        .input('name', sql.NVarChar, request.name).input('updatedBy', sql.NVarChar, actor.objectId)
        .query(`INSERT INTO ${T('Departments')} (Id, OrganizationId, Name, Status, CreatedAt, UpdatedAt, UpdatedBy)
          VALUES (@id, @organizationId, @name, 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
      return { id, organizationId, name: request.name, status: 'active' }
    })
  },

  async updateDepartment(
    actor: OrganizationAdminActor,
    organizationId: string,
    departmentId: string,
    request: UpdateDepartmentRequest,
  ): Promise<OrganizationAdminDepartment> {
    return await organizationMutation(async transaction => {
      requireStoredAuthority(await readOrganizationAuthority(transaction, actor), organizationId)
      const department = await transaction.request().input('organizationId', sql.NVarChar, organizationId)
        .input('departmentId', sql.NVarChar, departmentId)
        .query(`SELECT Id, Name, Status FROM ${lockTable('Departments')}
          WHERE Id = @departmentId AND OrganizationId = @organizationId`)
      if (!department.recordset[0]) throw new OrganizationAdminError('not_found', 'The department was not found in this organization.', 404)

      if (request.status === 'retired' && department.recordset[0].Status !== 'retired') {
        const activeDepartments = await transaction.request().input('organizationId', sql.NVarChar, organizationId)
          .query(`SELECT Id FROM ${lockTable('Departments')} WHERE OrganizationId = @organizationId AND Status = 'active'`)
        if (activeDepartments.recordset.length <= 1) {
          throw new OrganizationAdminError('conflict', 'An active organization must retain at least one active department.', 409)
        }
        const dependencies = await transaction.request().input('organizationId', sql.NVarChar, organizationId)
          .input('departmentId', sql.NVarChar, departmentId)
          .query(`SELECT 'job' AS Kind FROM ${T('Jobs')} WHERE OrganizationId = @organizationId AND DepartmentId = @departmentId
            UNION ALL SELECT 'membership' FROM ${T('DepartmentMemberships')}
              WHERE OrganizationId = @organizationId AND DepartmentId = @departmentId AND Status = 'active'
            UNION ALL SELECT 'default' FROM ${T('OrganizationMemberships')} om
              INNER JOIN ${T('DepartmentMemberships')} dm ON dm.Id = om.DefaultDepartmentMembershipId
              WHERE om.OrganizationId = @organizationId AND dm.DepartmentId = @departmentId AND om.Status = 'active'
            UNION ALL SELECT 'assignment' FROM ${T('RoleAssignments')}
              WHERE OrganizationId = @organizationId AND DepartmentId = @departmentId AND Status = 'active'`)
        if (dependencies.recordset.length > 0) {
          throw new OrganizationAdminError('conflict', 'Replace active jobs, memberships, defaults, and role scopes before retiring this department.', 409)
        }
      }

      const update = transaction.request().input('departmentId', sql.NVarChar, departmentId)
        .input('organizationId', sql.NVarChar, organizationId).input('updatedBy', sql.NVarChar, actor.objectId)
      const sets = ['UpdatedAt = SYSUTCDATETIME()', 'UpdatedBy = @updatedBy']
      if (request.name !== undefined) { update.input('name', sql.NVarChar, request.name); sets.push('Name = @name') }
      if (request.status !== undefined) { update.input('status', sql.NVarChar, request.status); sets.push('Status = @status') }
      await update.query(`UPDATE ${T('Departments')} SET ${sets.join(', ')}
        WHERE Id = @departmentId AND OrganizationId = @organizationId`)
      return {
        id: departmentId,
        organizationId,
        name: request.name ?? department.recordset[0].Name,
        status: request.status ?? department.recordset[0].Status,
      }
    })
  },

  async registerMembership(
    actor: OrganizationAdminActor,
    organizationId: string,
    request: RegisterOrganizationMembershipRequest,
  ): Promise<OrganizationAdminMembership> {
    return await organizationMutation(async transaction => {
      requireStoredAuthority(await readOrganizationAuthority(transaction, actor), organizationId)
      const target = await readTargetUser(transaction, actor, request.userObjectId)
      const organization = await transaction.request().input('organizationId', sql.NVarChar, organizationId)
        .query(`SELECT Id FROM ${lockTable('Organizations')} WHERE Id = @organizationId AND Status = 'active'`)
      if (!organization.recordset[0]) throw new OrganizationAdminError('not_found', 'The active organization was not found.', 404)

      const departmentRequest = transaction.request().input('organizationId', sql.NVarChar, organizationId)
      const placeholders = request.departmentIds.map((id, index) => {
        departmentRequest.input(`department${index}`, sql.NVarChar, id)
        return `@department${index}`
      })
      const departments = await departmentRequest.query(`SELECT Id FROM ${lockTable('Departments')}
        WHERE OrganizationId = @organizationId AND Status = 'active' AND Id IN (${placeholders.join(', ')})`)
      if (departments.recordset.length !== request.departmentIds.length) {
        throw new OrganizationAdminError('invalid_scope', 'Every department must be active in the selected organization.', 400)
      }

      const activeDepartments = await transaction.request().input('userId', sql.NVarChar, target.Id)
        .input('organizationId', sql.NVarChar, organizationId)
        .query(`SELECT Id, DepartmentId FROM ${lockTable('DepartmentMemberships')}
          WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)
      const desiredIds = new Set(request.departmentIds)
      const removedIds = activeDepartments.recordset
        .filter(row => !desiredIds.has(String(row.DepartmentId)))
        .map(row => String(row.DepartmentId))
      if (removedIds.length > 0) {
        const assignmentRequest = transaction.request().input('userId', sql.NVarChar, target.Id)
          .input('organizationId', sql.NVarChar, organizationId)
        const removedPlaceholders = removedIds.map((id, index) => {
          assignmentRequest.input(`removed${index}`, sql.NVarChar, id)
          return `@removed${index}`
        })
        const assignments = await assignmentRequest.query(`SELECT Id FROM ${lockTable('RoleAssignments')}
          WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'
            AND DepartmentId IN (${removedPlaceholders.join(', ')})`)
        if (assignments.recordset.length > 0) {
          throw new OrganizationAdminError('conflict', 'Revoke department-scoped roles before removing the membership.', 409)
        }
      }

      const membershipIds = new Map(activeDepartments.recordset
        .filter(row => desiredIds.has(String(row.DepartmentId)))
        .map(row => [String(row.DepartmentId), String(row.Id)]))
      for (const departmentId of request.departmentIds) {
        if (membershipIds.has(departmentId)) continue
        const existing = await transaction.request().input('userId', sql.NVarChar, target.Id)
          .input('organizationId', sql.NVarChar, organizationId).input('departmentId', sql.NVarChar, departmentId)
          .query(`SELECT Id FROM ${lockTable('DepartmentMemberships')}
            WHERE UserId = @userId AND OrganizationId = @organizationId AND DepartmentId = @departmentId`)
        const membershipId = existing.recordset[0]?.Id ?? randomUUID()
        if (existing.recordset[0]) {
          await transaction.request().input('id', sql.NVarChar, membershipId).input('updatedBy', sql.NVarChar, actor.objectId)
            .query(`UPDATE ${T('DepartmentMemberships')} SET Status = 'active', RevokedAt = NULL,
              EffectiveAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy WHERE Id = @id`)
        } else {
          await transaction.request().input('id', sql.NVarChar, membershipId).input('userId', sql.NVarChar, target.Id)
            .input('organizationId', sql.NVarChar, organizationId).input('departmentId', sql.NVarChar, departmentId)
            .input('updatedBy', sql.NVarChar, actor.objectId)
            .query(`INSERT INTO ${T('DepartmentMemberships')} (Id, UserId, OrganizationId, DepartmentId, Status, EffectiveAt, UpdatedBy)
              VALUES (@id, @userId, @organizationId, @departmentId, 'active', SYSUTCDATETIME(), @updatedBy)`)
        }
        membershipIds.set(departmentId, String(membershipId))
      }

      const defaultMembershipId = membershipIds.get(request.defaultDepartmentId)
      if (!defaultMembershipId) throw new OrganizationAdminError('invalid_scope', 'The explicit default is not an active membership.', 400)
      const memberships = await transaction.request().input('userId', sql.NVarChar, target.Id)
        .input('organizationId', sql.NVarChar, organizationId)
        .query(`SELECT Id FROM ${lockTable('OrganizationMemberships')}
          WHERE UserId = @userId AND OrganizationId = @organizationId`)
      if (memberships.recordset[0]) {
        await transaction.request().input('id', sql.NVarChar, memberships.recordset[0].Id)
          .input('defaultId', sql.NVarChar, defaultMembershipId).input('updatedBy', sql.NVarChar, actor.objectId)
          .query(`UPDATE ${T('OrganizationMemberships')} SET Status = 'active', RevokedAt = NULL,
            EffectiveAt = SYSUTCDATETIME(), DefaultDepartmentMembershipId = @defaultId, UpdatedBy = @updatedBy WHERE Id = @id`)
      } else {
        await transaction.request().input('id', sql.NVarChar, randomUUID()).input('userId', sql.NVarChar, target.Id)
          .input('organizationId', sql.NVarChar, organizationId).input('defaultId', sql.NVarChar, defaultMembershipId)
          .input('updatedBy', sql.NVarChar, actor.objectId)
          .query(`INSERT INTO ${T('OrganizationMemberships')} (Id, UserId, OrganizationId, DefaultDepartmentMembershipId, Status, EffectiveAt, UpdatedBy)
            VALUES (@id, @userId, @organizationId, @defaultId, 'active', SYSUTCDATETIME(), @updatedBy)`)
      }
      for (const membership of activeDepartments.recordset) {
        if (desiredIds.has(String(membership.DepartmentId))) continue
        await transaction.request().input('id', sql.NVarChar, membership.Id).input('updatedBy', sql.NVarChar, actor.objectId)
          .query(`UPDATE ${T('DepartmentMemberships')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy WHERE Id = @id`)
      }
      await advanceAuthorizationVersion(transaction, target.Id)
      return { userObjectId: request.userObjectId, organizationId, departmentIds: request.departmentIds, defaultDepartmentId: request.defaultDepartmentId }
    })
  },

  async grantRole(
    actor: OrganizationAdminActor,
    organizationId: string,
    request: GrantOrganizationRoleRequest,
  ): Promise<OrganizationAdminRoleAssignment> {
    return await organizationMutation(async transaction => {
      const authority = await readOrganizationAuthority(transaction, actor)
      requireStoredAuthority(authority, organizationId)
      if (request.role === 'organization_admin' && !authority.globalAdmin) {
        throw new OrganizationAdminError('forbidden', 'Only an application Admin may delegate Organization Admin.', 403)
      }
      const target = await readTargetUser(transaction, actor, request.userObjectId)
      const membership = await transaction.request().input('userId', sql.NVarChar, target.Id)
        .input('organizationId', sql.NVarChar, organizationId)
        .query(`SELECT Id FROM ${lockTable('OrganizationMemberships')}
          WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)
      if (!membership.recordset[0]) throw new OrganizationAdminError('conflict', 'Active organization membership is required.', 409)
      if (request.departmentId) {
        const department = await transaction.request().input('userId', sql.NVarChar, target.Id)
          .input('organizationId', sql.NVarChar, organizationId).input('departmentId', sql.NVarChar, request.departmentId)
          .query(`SELECT dm.Id FROM ${lockTable('DepartmentMemberships')} dm
            INNER JOIN ${T('Departments')} d ON d.Id = dm.DepartmentId AND d.OrganizationId = dm.OrganizationId AND d.Status = 'active'
            WHERE dm.UserId = @userId AND dm.OrganizationId = @organizationId
              AND dm.DepartmentId = @departmentId AND dm.Status = 'active'`)
        if (!department.recordset[0]) throw new OrganizationAdminError('invalid_scope', 'Role department must be an active membership.', 400)
      }
      const existing = await transaction.request().input('tenantId', sql.NVarChar, actor.tenantId)
        .input('objectId', sql.NVarChar, request.userObjectId).input('organizationId', sql.NVarChar, organizationId)
        .input('role', sql.NVarChar, request.role).input('departmentId', sql.NVarChar, request.departmentId)
        .query(`SELECT Id FROM ${lockTable('RoleAssignments')}
          WHERE TenantId = @tenantId AND UserObjectId = @objectId AND OrganizationId = @organizationId
            AND Role = @role AND Source = 'delegated' AND Status = 'active'
            AND ((DepartmentId = @departmentId) OR (DepartmentId IS NULL AND @departmentId IS NULL))`)
      const id = existing.recordset[0]?.Id ?? randomUUID()
      if (!existing.recordset[0]) {
        await transaction.request().input('id', sql.NVarChar, id).input('userId', sql.NVarChar, target.Id)
          .input('tenantId', sql.NVarChar, actor.tenantId).input('objectId', sql.NVarChar, request.userObjectId)
          .input('role', sql.NVarChar, request.role).input('organizationId', sql.NVarChar, organizationId)
          .input('departmentId', sql.NVarChar, request.departmentId).input('updatedBy', sql.NVarChar, actor.objectId)
          .query(`INSERT INTO ${T('RoleAssignments')} (Id, UserId, TenantId, UserObjectId, Role, OrganizationId, DepartmentId, Source, Status, EffectiveAt, CreatedAt, UpdatedAt, UpdatedBy)
            VALUES (@id, @userId, @tenantId, @objectId, @role, @organizationId, @departmentId, 'delegated', 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
        await advanceAuthorizationVersion(transaction, target.Id)
      }
      return { id: String(id), userObjectId: request.userObjectId, role: request.role, organizationId, departmentId: request.departmentId, source: 'delegated', status: 'active' }
    })
  },

  async revokeRole(actor: OrganizationAdminActor, organizationId: string, assignmentId: string): Promise<void> {
    await organizationMutation(async transaction => {
      const authority = await readOrganizationAuthority(transaction, actor)
      requireStoredAuthority(authority, organizationId)
      const assignment = await transaction.request().input('id', sql.NVarChar, assignmentId)
        .input('organizationId', sql.NVarChar, organizationId)
        .query(`SELECT Id, UserId, Role FROM ${lockTable('RoleAssignments')}
          WHERE Id = @id AND OrganizationId = @organizationId AND Source = 'delegated' AND Status = 'active'`)
      if (!assignment.recordset[0]) throw new OrganizationAdminError('not_found', 'The delegated role assignment was not found.', 404)
      if (assignment.recordset[0].Role === 'organization_admin' && !authority.globalAdmin) {
        throw new OrganizationAdminError('forbidden', 'Only an application Admin may revoke Organization Admin.', 403)
      }
      await transaction.request().input('id', sql.NVarChar, assignmentId).input('updatedBy', sql.NVarChar, actor.objectId)
        .query(`UPDATE ${T('RoleAssignments')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(),
          UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy WHERE Id = @id`)
      await advanceAuthorizationVersion(transaction, assignment.recordset[0].UserId)
    })
  },

  async getById(organizationId: string): Promise<Organization | undefined> {
    const pool = await getPool()
    const result = await pool.request().input('id', sql.NVarChar, organizationId)
      .query(`SELECT * FROM ${T('Organizations')} WHERE Id = @id`)
    const row = result.recordset[0]
    return row && { organizationId: row.Id, name: row.Name, status: row.Status, createdAt: asIso(row.CreatedAt), updatedAt: asIso(row.UpdatedAt), updatedBy: row.UpdatedBy }
  },

  async getAuthorizationMemberships(userId: string): Promise<AuthorizationMembership[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('userId', sql.NVarChar, userId)
      .query(`SELECT o.Id AS OrganizationId, o.Name AS OrganizationName,
          defaultDm.DepartmentId AS DefaultDepartmentId,
          d.Id AS DepartmentId, d.Name AS DepartmentName
        FROM ${T('OrganizationMemberships')} om
        INNER JOIN ${T('Organizations')} o ON o.Id = om.OrganizationId AND o.Status = 'active'
        LEFT JOIN ${T('DepartmentMemberships')} defaultDm
          ON defaultDm.Id = om.DefaultDepartmentMembershipId
          AND defaultDm.UserId = om.UserId
          AND defaultDm.OrganizationId = om.OrganizationId
        INNER JOIN ${T('DepartmentMemberships')} dm
          ON dm.UserId = om.UserId AND dm.OrganizationId = om.OrganizationId AND dm.Status = 'active'
        INNER JOIN ${T('Departments')} d
          ON d.Id = dm.DepartmentId AND d.OrganizationId = dm.OrganizationId AND d.Status = 'active'
        WHERE om.UserId = @userId AND om.Status = 'active'
        ORDER BY o.Name, d.Name`)

    const memberships = new Map<string, AuthorizationMembership>()
    for (const row of result.recordset) {
      let membership = memberships.get(row.OrganizationId)
      if (!membership) {
        membership = {
          organizationId: row.OrganizationId,
          organizationName: row.OrganizationName,
          defaultDepartmentId: row.DefaultDepartmentId ?? null,
          departments: [],
        }
        memberships.set(row.OrganizationId, membership)
      }
      membership.departments.push({
        departmentId: row.DepartmentId,
        departmentName: row.DepartmentName,
      })
    }
    return [...memberships.values()]
  },

  async createOrganizationWithDepartment(organization: Organization, department: Department): Promise<void> {
    if (department.organizationId !== organization.organizationId) throw new Error('Department must belong to the organization being created.')
    const pool = await getPool()
    const transaction = pool.transaction()
    await transaction.begin()
    try {
      await transaction.request().input('id', sql.NVarChar, organization.organizationId).input('name', sql.NVarChar, organization.name)
        .input('status', sql.NVarChar, organization.status).input('updatedBy', sql.NVarChar, organization.updatedBy)
        .query(`INSERT INTO ${T('Organizations')} (Id, Name, Status, CreatedAt, UpdatedAt, UpdatedBy) VALUES (@id, @name, @status, SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
      await transaction.request().input('id', sql.NVarChar, department.departmentId).input('organizationId', sql.NVarChar, department.organizationId)
        .input('name', sql.NVarChar, department.name).input('status', sql.NVarChar, department.status).input('updatedBy', sql.NVarChar, department.updatedBy)
        .query(`INSERT INTO ${T('Departments')} (Id, OrganizationId, Name, Status, CreatedAt, UpdatedAt, UpdatedBy) VALUES (@id, @organizationId, @name, @status, SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
      await transaction.commit()
    } catch (error) { await transaction.rollback(); throw error }
  },

  async activateMemberships(
    organizationMembership: OrganizationMembership,
    departmentMemberships: readonly DepartmentMembership[],
    defaultDepartmentId: string,
  ): Promise<void> {
    if (organizationMembership.status !== 'active' || organizationMembership.revokedAt) throw new Error('Organization membership must be active.')
    if (departmentMemberships.length === 0) throw new Error('At least one active department membership is required.')
    if (departmentMemberships.some(membership => membership.userId !== organizationMembership.userId
      || membership.organizationId !== organizationMembership.organizationId
      || membership.status !== 'active'
      || membership.revokedAt)) throw new Error('Membership scopes and statuses must agree.')
    if (new Set(departmentMemberships.map(membership => membership.membershipId)).size !== departmentMemberships.length
      || new Set(departmentMemberships.map(membership => membership.departmentId)).size !== departmentMemberships.length) throw new Error('Department memberships must be unique.')
    const defaultMembership = departmentMemberships.find(membership => membership.departmentId === defaultDepartmentId)
    if (!defaultMembership) throw new Error('Default department must be part of the active membership transition.')

    const pool = await getPool()
    const transaction = pool.transaction()
    await transaction.begin()
    try {
      for (const departmentMembership of departmentMemberships) {
        const department = await transaction.request().input('id', sql.NVarChar, departmentMembership.departmentId).input('organizationId', sql.NVarChar, departmentMembership.organizationId)
          .query(`SELECT Id FROM ${T('Departments')} WHERE Id = @id AND OrganizationId = @organizationId AND Status = 'active'`)
        if (!department.recordset[0]) throw new Error('All departments must belong to the active organization.')
      }
      for (const departmentMembership of departmentMemberships) {
        await transaction.request().input('id', sql.NVarChar, departmentMembership.membershipId).input('userId', sql.NVarChar, departmentMembership.userId).input('organizationId', sql.NVarChar, departmentMembership.organizationId).input('departmentId', sql.NVarChar, departmentMembership.departmentId).input('updatedBy', sql.NVarChar, departmentMembership.updatedBy)
          .query(`INSERT INTO ${T('DepartmentMemberships')} (Id, UserId, OrganizationId, DepartmentId, Status, EffectiveAt, UpdatedBy) VALUES (@id, @userId, @organizationId, @departmentId, 'active', SYSUTCDATETIME(), @updatedBy)`)
      }
      await transaction.request().input('id', sql.NVarChar, organizationMembership.membershipId).input('userId', sql.NVarChar, organizationMembership.userId).input('organizationId', sql.NVarChar, organizationMembership.organizationId).input('defaultDepartmentMembershipId', sql.NVarChar, defaultMembership.membershipId).input('updatedBy', sql.NVarChar, organizationMembership.updatedBy)
        .query(`INSERT INTO ${T('OrganizationMemberships')} (Id, UserId, OrganizationId, DefaultDepartmentMembershipId, Status, EffectiveAt, UpdatedBy) VALUES (@id, @userId, @organizationId, @defaultDepartmentMembershipId, 'active', SYSUTCDATETIME(), @updatedBy)`)
      await transaction.commit()
    } catch (error) { await transaction.rollback(); throw error }
  },

  async replaceDefaultDepartment(userId: string, organizationId: string, defaultDepartmentId: string, updatedBy: string): Promise<void> {
    const pool = await getPool()
    const transaction = pool.transaction()
    await transaction.begin()
    try {
      const defaultMembership = await transaction.request().input('userId', sql.NVarChar, userId).input('organizationId', sql.NVarChar, organizationId).input('departmentId', sql.NVarChar, defaultDepartmentId)
        .query(`SELECT dm.Id FROM ${T('DepartmentMemberships')} dm
          INNER JOIN ${T('Departments')} d ON d.Id = dm.DepartmentId AND d.OrganizationId = dm.OrganizationId AND d.Status = 'active'
          WHERE dm.UserId = @userId AND dm.OrganizationId = @organizationId AND dm.DepartmentId = @departmentId AND dm.Status = 'active'`)
      if (!defaultMembership.recordset[0]) throw new Error('Default department must be an active membership in the organization.')
      const result = await transaction.request().input('userId', sql.NVarChar, userId).input('organizationId', sql.NVarChar, organizationId).input('defaultDepartmentMembershipId', sql.NVarChar, defaultMembership.recordset[0].Id).input('updatedBy', sql.NVarChar, updatedBy)
        .query(`UPDATE ${T('OrganizationMemberships')} SET DefaultDepartmentMembershipId = @defaultDepartmentMembershipId, UpdatedBy = @updatedBy
          WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)
      if ((result.rowsAffected?.[0] ?? 0) === 0) throw new Error('Active organization membership was not found.')
      await transaction.commit()
    } catch (error) { await transaction.rollback(); throw error }
  },

  async revokeMemberships(userId: string, organizationId: string, updatedBy: string): Promise<void> {
    const pool = await getPool()
    const transaction = pool.transaction()
    await transaction.begin()
    try {
      const organizationResult = await transaction.request().input('userId', sql.NVarChar, userId).input('organizationId', sql.NVarChar, organizationId).input('updatedBy', sql.NVarChar, updatedBy)
        .query(`UPDATE ${T('OrganizationMemberships')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy
          WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)
      if ((organizationResult.rowsAffected?.[0] ?? 0) === 0) throw new Error('Active organization membership was not found.')
      await transaction.request().input('userId', sql.NVarChar, userId).input('organizationId', sql.NVarChar, organizationId).input('updatedBy', sql.NVarChar, updatedBy)
        .query(`UPDATE ${T('DepartmentMemberships')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy
          WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)
      await transaction.commit()
    } catch (error) { await transaction.rollback(); throw error }
  },
}