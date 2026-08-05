import { randomUUID } from 'node:crypto'
import type {
  EntraAccessUser,
  EntraAccessUserPage,
  EntraOrganizationAccess,
  PutOrganizationAccessRequest,
  UpdateEntraAccessUserRequest,
} from '../../../src/types/index.js'
import {
  AccessManagementActor,
  EntraAccessError,
  type EntraAccessSearchOptions,
} from '../../services/entra-access-management.js'
import { getPool, isAzureSql, sql } from '../db.js'
import { T } from '../table-names.js'
import type { StorageExecutor } from '../types.js'

export interface EntraAccessManagementRepository {
  list(actor: AccessManagementActor, options: EntraAccessSearchOptions): Promise<EntraAccessUserPage>
  get(actor: AccessManagementActor, objectId: string): Promise<EntraAccessUser | undefined>
  updateUser(
    actor: AccessManagementActor,
    objectId: string,
    request: UpdateEntraAccessUserRequest,
  ): Promise<EntraAccessUser>
  putOrganizationAccess(
    actor: AccessManagementActor,
    objectId: string,
    organizationId: string,
    request: PutOrganizationAccessRequest,
  ): Promise<EntraAccessUser>
  revokeRole(
    actor: AccessManagementActor,
    objectId: string,
    organizationId: string,
    assignmentId: string,
    expectedVersion: number,
  ): Promise<EntraAccessUser>
}

interface StoredAuthority {
  globalAdmin: boolean
  organizationAdminIds: string[]
}

function asBoolean(value: unknown): boolean {
  return value === true || value === 1
}

function lockTable(tableName: string): string {
  const table = T(tableName)
  return isAzureSql ? `${table} WITH (UPDLOCK, HOLDLOCK)` : table
}

async function appendAudit(
  executor: StorageExecutor,
  actor: AccessManagementActor,
  action: 'access_management_succeeded' | 'access_management_failed',
  objectId: string,
  operation: string,
  code: string,
): Promise<void> {
  await executor.request()
    .input('id', sql.NVarChar, randomUUID())
    .input('actor', sql.NVarChar, actor.objectId)
    .input('action', sql.NVarChar, action)
    .input('entityType', sql.NVarChar, 'entra_access_user')
    .input('entityId', sql.NVarChar, objectId)
    .input('details', sql.NVarChar, JSON.stringify({ operation, code }))
    .input('correlationId', sql.NVarChar, actor.correlationId)
    .query(`INSERT INTO ${T('ProcessingEvents')} (Id, Actor, Action, EntityType, EntityId, DetailsJson, Timestamp, CorrelationId)
      VALUES (@id, @actor, @action, @entityType, @entityId, @details, SYSUTCDATETIME(), @correlationId)`)
}

async function readAuthority(executor: StorageExecutor, actor: AccessManagementActor): Promise<StoredAuthority> {
  const user = await executor.request()
    .input('tenantId', sql.NVarChar, actor.tenantId)
    .input('objectId', sql.NVarChar, actor.objectId)
    .query(`SELECT Id, IsActive FROM ${lockTable('Users')}
      WHERE AuthenticationProvider = 'entra' AND EntraTenantId = @tenantId AND EntraObjectId = @objectId`)
  if (!user.recordset[0] || !asBoolean(user.recordset[0].IsActive)) {
    throw new EntraAccessError('forbidden', 'The actor is not an active Entra identity.', 403)
  }

  const assignments = await executor.request()
    .input('tenantId', sql.NVarChar, actor.tenantId)
    .input('objectId', sql.NVarChar, actor.objectId)
    .input('userId', sql.NVarChar, user.recordset[0].Id)
    .query(`SELECT ra.Role, ra.OrganizationId
      FROM ${lockTable('RoleAssignments')} ra
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

function requireAuthority(authority: StoredAuthority, organizationId?: string): void {
  if (authority.globalAdmin) return
  if (organizationId && authority.organizationAdminIds.includes(organizationId)) return
  throw new EntraAccessError('forbidden', 'The actor does not have authority for this operation.', 403)
}

function filterAggregateScopes(
  aggregate: EntraAccessUser,
  authority: StoredAuthority,
): EntraAccessUser {
  if (authority.globalAdmin || aggregate.organizations.length === 0) return aggregate
  return {
    ...aggregate,
    organizations: aggregate.organizations.filter(item =>
      authority.organizationAdminIds.includes(item.organizationId)),
  }
}

async function readTarget(
  executor: StorageExecutor,
  tenantId: string,
  objectId: string,
): Promise<any | undefined> {
  const result = await executor.request()
    .input('tenantId', sql.NVarChar, tenantId)
    .input('objectId', sql.NVarChar, objectId)
    .query(`SELECT * FROM ${lockTable('Users')}
      WHERE AuthenticationProvider = 'entra' AND EntraTenantId = @tenantId AND EntraObjectId = @objectId`)
  return result.recordset[0]
}

async function readAggregate(
  executor: StorageExecutor,
  tenantId: string,
  objectId: string,
): Promise<EntraAccessUser | undefined> {
  const user = await readTarget(executor, tenantId, objectId)
  if (!user) return undefined

  const memberships = await executor.request()
    .input('userId', sql.NVarChar, user.Id)
    .query(`SELECT om.OrganizationId, om.Status, defaultDm.DepartmentId AS DefaultDepartmentId,
        dm.DepartmentId
      FROM ${T('OrganizationMemberships')} om
      LEFT JOIN ${T('DepartmentMemberships')} defaultDm
        ON defaultDm.Id = om.DefaultDepartmentMembershipId
        AND defaultDm.UserId = om.UserId AND defaultDm.OrganizationId = om.OrganizationId
      LEFT JOIN ${T('DepartmentMemberships')} dm
        ON dm.UserId = om.UserId AND dm.OrganizationId = om.OrganizationId AND dm.Status = 'active'
      WHERE om.UserId = @userId AND om.Status = 'active'
      ORDER BY om.OrganizationId, dm.DepartmentId`)
  const assignments = await executor.request()
    .input('tenantId', sql.NVarChar, tenantId)
    .input('objectId', sql.NVarChar, objectId)
    .query(`SELECT Id, Role, OrganizationId, DepartmentId, Source, Status
      FROM ${T('RoleAssignments')}
      WHERE TenantId = @tenantId AND UserObjectId = @objectId AND Status = 'active'
        AND Source IN ('delegated', 'group') AND Role <> 'admin'
      ORDER BY OrganizationId, Role, DepartmentId`)

  const organizations = new Map<string, EntraOrganizationAccess>()
  for (const row of memberships.recordset) {
    let organization = organizations.get(row.OrganizationId)
    if (!organization) {
      organization = {
        organizationId: row.OrganizationId,
        status: row.Status,
        departmentIds: [],
        defaultDepartmentId: row.DefaultDepartmentId ?? null,
        roleAssignments: [],
      }
      organizations.set(row.OrganizationId, organization)
    }
    if (row.DepartmentId && !organization.departmentIds.includes(row.DepartmentId)) {
      organization.departmentIds.push(row.DepartmentId)
    }
  }
  for (const row of assignments.recordset) {
    const organization = organizations.get(row.OrganizationId)
    if (!organization) continue
    organization.roleAssignments.push({
      id: row.Id,
      role: row.Role,
      organizationId: row.OrganizationId,
      departmentId: row.DepartmentId ?? null,
      source: row.Source,
      status: row.Status,
    })
  }
  return {
    objectId: user.EntraObjectId,
    username: user.Username,
    fullName: user.FullName,
    email: user.Email || null,
    isActive: asBoolean(user.IsActive),
    authorizationVersion: Number(user.AuthorizationVersion ?? 0),
    organizations: [...organizations.values()],
  }
}

async function requireTargetVersion(
  executor: StorageExecutor,
  actor: AccessManagementActor,
  objectId: string,
  expectedVersion: number,
): Promise<any> {
  const target = await readTarget(executor, actor.tenantId, objectId)
  if (!target) throw new EntraAccessError('not_found', 'The tenant-verified Entra profile was not found.', 404)
  if (Number(target.AuthorizationVersion) !== expectedVersion) {
    throw new EntraAccessError('version_conflict', 'Authorization state changed. Refresh and retry.', 409)
  }
  return target
}

async function advanceVersion(executor: StorageExecutor, userId: string, expectedVersion: number): Promise<void> {
  const result = await executor.request()
    .input('userId', sql.NVarChar, userId)
    .input('expectedVersion', sql.Int, expectedVersion)
    .query(`UPDATE ${T('Users')} SET AuthorizationVersion = AuthorizationVersion + 1
      WHERE Id = @userId AND AuthorizationVersion = @expectedVersion`)
  if ((result.rowsAffected?.[0] ?? 0) !== 1) {
    throw new EntraAccessError('version_conflict', 'Authorization state changed. Refresh and retry.', 409)
  }
}

async function mutate<T>(
  actor: AccessManagementActor,
  objectId: string,
  operation: string,
  work: (transaction: StorageExecutor) => Promise<T>,
): Promise<T> {
  const pool = await getPool()
  const transaction = pool.transaction()
  await transaction.begin(isAzureSql ? sql.ISOLATION_LEVEL.SERIALIZABLE : undefined)
  try {
    const result = await work(transaction)
    await appendAudit(transaction, actor, 'access_management_succeeded', objectId, operation, 'success')
    await transaction.commit()
    return result
  } catch (error) {
    await transaction.rollback()
    try {
      await appendAudit(
        pool,
        actor,
        'access_management_failed',
        objectId,
        operation,
        error instanceof EntraAccessError ? error.code : 'internal_error',
      )
    } catch {
      // The original operation error remains authoritative if audit persistence is unavailable.
    }
    throw error
  }
}

async function updateProfile(
  executor: StorageExecutor,
  userId: string,
  profile: PutOrganizationAccessRequest['profile'],
): Promise<void> {
  await executor.request()
    .input('userId', sql.NVarChar, userId)
    .input('username', sql.NVarChar, profile.username)
    .input('fullName', sql.NVarChar, profile.fullName)
    .input('email', sql.NVarChar, profile.email)
    .query(`UPDATE ${T('Users')} SET Username = @username, FullName = @fullName, Email = @email WHERE Id = @userId`)
}

async function convergeMembership(
  executor: StorageExecutor,
  actor: AccessManagementActor,
  target: any,
  organizationId: string,
  request: PutOrganizationAccessRequest,
): Promise<void> {
  const organization = await executor.request()
    .input('organizationId', sql.NVarChar, organizationId)
    .query(`SELECT Id FROM ${lockTable('Organizations')} WHERE Id = @organizationId AND Status = 'active'`)
  if (!organization.recordset[0]) throw new EntraAccessError('not_found', 'The organization was not found.', 404)

  const activeOrganization = await executor.request()
    .input('userId', sql.NVarChar, target.Id)
    .input('organizationId', sql.NVarChar, organizationId)
    .query(`SELECT Id FROM ${lockTable('OrganizationMemberships')}
      WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)
  const activeDepartments = await executor.request()
    .input('userId', sql.NVarChar, target.Id)
    .input('organizationId', sql.NVarChar, organizationId)
    .query(`SELECT Id, DepartmentId FROM ${lockTable('DepartmentMemberships')}
      WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)

  if (request.membership.status === 'revoked') {
    await executor.request().input('userId', sql.NVarChar, target.Id)
      .input('organizationId', sql.NVarChar, organizationId).input('updatedBy', sql.NVarChar, actor.objectId)
      .query(`UPDATE ${T('OrganizationMemberships')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy
        WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)
    await executor.request().input('userId', sql.NVarChar, target.Id)
      .input('organizationId', sql.NVarChar, organizationId).input('updatedBy', sql.NVarChar, actor.objectId)
      .query(`UPDATE ${T('DepartmentMemberships')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy
        WHERE UserId = @userId AND OrganizationId = @organizationId AND Status = 'active'`)
    return
  }

  const departmentRequest = executor.request().input('organizationId', sql.NVarChar, organizationId)
  const placeholders = request.membership.departmentIds.map((departmentId, index) => {
    departmentRequest.input(`department${index}`, sql.NVarChar, departmentId)
    return `@department${index}`
  })
  const departments = await departmentRequest.query(`SELECT Id FROM ${lockTable('Departments')}
    WHERE OrganizationId = @organizationId AND Status = 'active' AND Id IN (${placeholders.join(', ')})`)
  if (departments.recordset.length !== request.membership.departmentIds.length) {
    throw new EntraAccessError('invalid_scope', 'Every department must be active in the selected organization.', 400)
  }

  const desiredIds = new Set(request.membership.departmentIds)
  for (const membership of activeDepartments.recordset) {
    if (desiredIds.has(membership.DepartmentId)) continue
    await executor.request().input('id', sql.NVarChar, membership.Id).input('updatedBy', sql.NVarChar, actor.objectId)
      .query(`UPDATE ${T('DepartmentMemberships')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy WHERE Id = @id`)
  }
  const membershipIds = new Map(activeDepartments.recordset
    .filter(row => desiredIds.has(row.DepartmentId))
    .map(row => [String(row.DepartmentId), String(row.Id)]))
  for (const departmentId of request.membership.departmentIds) {
    if (membershipIds.has(departmentId)) continue
    const membershipId = randomUUID()
    await executor.request().input('id', sql.NVarChar, membershipId).input('userId', sql.NVarChar, target.Id)
      .input('organizationId', sql.NVarChar, organizationId).input('departmentId', sql.NVarChar, departmentId)
      .input('updatedBy', sql.NVarChar, actor.objectId)
      .query(`INSERT INTO ${T('DepartmentMemberships')} (Id, UserId, OrganizationId, DepartmentId, Status, EffectiveAt, UpdatedBy)
        VALUES (@id, @userId, @organizationId, @departmentId, 'active', SYSUTCDATETIME(), @updatedBy)`)
    membershipIds.set(departmentId, membershipId)
  }

  const defaultMembershipId = membershipIds.get(request.membership.defaultDepartmentId!)
  if (!defaultMembershipId) throw new EntraAccessError('invalid_scope', 'The explicit default is not active.', 400)
  if (activeOrganization.recordset[0]) {
    await executor.request().input('id', sql.NVarChar, activeOrganization.recordset[0].Id)
      .input('defaultId', sql.NVarChar, defaultMembershipId).input('updatedBy', sql.NVarChar, actor.objectId)
      .query(`UPDATE ${T('OrganizationMemberships')} SET DefaultDepartmentMembershipId = @defaultId, UpdatedBy = @updatedBy WHERE Id = @id`)
  } else {
    await executor.request().input('id', sql.NVarChar, randomUUID()).input('userId', sql.NVarChar, target.Id)
      .input('organizationId', sql.NVarChar, organizationId).input('defaultId', sql.NVarChar, defaultMembershipId)
      .input('updatedBy', sql.NVarChar, actor.objectId)
      .query(`INSERT INTO ${T('OrganizationMemberships')} (Id, UserId, OrganizationId, DefaultDepartmentMembershipId, Status, EffectiveAt, UpdatedBy)
        VALUES (@id, @userId, @organizationId, @defaultId, 'active', SYSUTCDATETIME(), @updatedBy)`)
  }
}

async function convergeDelegatedRoles(
  executor: StorageExecutor,
  actor: AccessManagementActor,
  target: any,
  organizationId: string,
  request: PutOrganizationAccessRequest,
): Promise<void> {
  const existing = await executor.request().input('tenantId', sql.NVarChar, actor.tenantId)
    .input('objectId', sql.NVarChar, target.EntraObjectId).input('organizationId', sql.NVarChar, organizationId)
    .query(`SELECT Id, Role, DepartmentId FROM ${lockTable('RoleAssignments')}
      WHERE TenantId = @tenantId AND UserObjectId = @objectId AND OrganizationId = @organizationId
        AND Source = 'delegated' AND Status = 'active'`)
  const desired = request.membership.status === 'active' ? request.roleAssignments : []
  const desiredKeys = new Set(desired.map(item => `${item.role}:${item.departmentId ?? ''}`))
  for (const assignment of existing.recordset) {
    if (desiredKeys.has(`${assignment.Role}:${assignment.DepartmentId ?? ''}`)) continue
    await executor.request().input('id', sql.NVarChar, assignment.Id).input('updatedBy', sql.NVarChar, actor.objectId)
      .query(`UPDATE ${T('RoleAssignments')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy WHERE Id = @id`)
  }
  const existingKeys = new Set(existing.recordset.map(row => `${row.Role}:${row.DepartmentId ?? ''}`))
  for (const assignment of desired) {
    const key = `${assignment.role}:${assignment.departmentId ?? ''}`
    if (existingKeys.has(key)) continue
    await executor.request().input('id', sql.NVarChar, randomUUID()).input('userId', sql.NVarChar, target.Id)
      .input('tenantId', sql.NVarChar, actor.tenantId).input('objectId', sql.NVarChar, target.EntraObjectId)
      .input('role', sql.NVarChar, assignment.role).input('organizationId', sql.NVarChar, organizationId)
      .input('departmentId', sql.NVarChar, assignment.departmentId).input('updatedBy', sql.NVarChar, actor.objectId)
      .query(`INSERT INTO ${T('RoleAssignments')} (Id, UserId, TenantId, UserObjectId, Role, OrganizationId, DepartmentId, Source, Status, EffectiveAt, CreatedAt, UpdatedAt, UpdatedBy)
        VALUES (@id, @userId, @tenantId, @objectId, @role, @organizationId, @departmentId, 'delegated', 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
  }
}

const repository: EntraAccessManagementRepository = {
  async list(actor, options) {
    const pool = await getPool()
    const authority = await readAuthority(pool, actor)
    if (options.organizationId) {
      requireAuthority(authority, options.organizationId)
    } else if (!authority.globalAdmin && authority.organizationAdminIds.length === 0) {
      requireAuthority(authority)
    }
    const request = pool.request().input('tenantId', sql.NVarChar, actor.tenantId)
      .input('take', sql.Int, options.limit + 1)
    const predicates = ["u.AuthenticationProvider = 'entra'", 'u.EntraTenantId = @tenantId']
    if (options.search) {
      request.input('search', sql.NVarChar, `%${options.search}%`)
      predicates.push('(u.Username LIKE @search OR u.FullName LIKE @search OR u.Email LIKE @search)')
    }
    if (options.cursor) { request.input('cursor', sql.NVarChar, options.cursor); predicates.push('u.Id > @cursor') }
    if (options.organizationId) {
      request.input('organizationId', sql.NVarChar, options.organizationId)
      predicates.push(`EXISTS (SELECT 1 FROM ${T('OrganizationMemberships')} om WHERE om.UserId = u.Id AND om.OrganizationId = @organizationId AND om.Status = 'active')`)
    }
    if (!authority.globalAdmin) {
      const scope = authority.organizationAdminIds.map((id, index) => {
        request.input(`scope${index}`, sql.NVarChar, id)
        return `@scope${index}`
      })
      predicates.push(`(NOT EXISTS (SELECT 1 FROM ${T('OrganizationMemberships')} pending WHERE pending.UserId = u.Id AND pending.Status = 'active')
        OR EXISTS (SELECT 1 FROM ${T('OrganizationMemberships')} scoped WHERE scoped.UserId = u.Id AND scoped.Status = 'active' AND scoped.OrganizationId IN (${scope.join(', ')})))`)
    }
    if (options.status === 'disabled') predicates.push('u.IsActive = 0')
    if (options.status === 'pending') predicates.push(`u.IsActive = 1 AND NOT EXISTS (SELECT 1 FROM ${T('OrganizationMemberships')} pending WHERE pending.UserId = u.Id AND pending.Status = 'active')`)
    if (options.status === 'active') predicates.push(`u.IsActive = 1 AND EXISTS (SELECT 1 FROM ${T('OrganizationMemberships')} activeMembership WHERE activeMembership.UserId = u.Id AND activeMembership.Status = 'active')`)
    const result = await request.query(`SELECT u.Id AS CursorId, u.EntraObjectId FROM ${T('Users')} u
      WHERE ${predicates.join(' AND ')} ORDER BY u.Id OFFSET 0 ROWS FETCH NEXT @take ROWS ONLY`)
    const pageRows = result.recordset.slice(0, options.limit)
    const items = await Promise.all(pageRows.map(async (row: { CursorId: string; EntraObjectId: string }) => {
      const aggregate = await readAggregate(pool, actor.tenantId, row.EntraObjectId)
      return aggregate ? filterAggregateScopes(aggregate, authority) : undefined
    }))
    return {
      items: items.filter((item): item is EntraAccessUser => item !== undefined),
      nextCursor: result.recordset.length > options.limit ? pageRows.at(-1)?.CursorId ?? null : null,
    }
  },

  async get(actor, objectId) {
    const pool = await getPool()
    const authority = await readAuthority(pool, actor)
    const aggregate = await readAggregate(pool, actor.tenantId, objectId)
    if (!aggregate) return undefined
    if (!authority.globalAdmin) {
      const visible = aggregate.organizations.length === 0
        || aggregate.organizations.some(item => authority.organizationAdminIds.includes(item.organizationId))
      if (!visible) throw new EntraAccessError('forbidden', 'The profile is outside the actor scope.', 403)
    }
    return filterAggregateScopes(aggregate, authority)
  },

  async updateUser(actor, objectId, request) {
    await mutate(actor, objectId, 'update_user', async transaction => {
      const authority = await readAuthority(transaction, actor)
      requireAuthority(authority)
      const target = await requireTargetVersion(transaction, actor, objectId, request.expectedVersion)
      const update = transaction.request().input('userId', sql.NVarChar, target.Id)
      const sets: string[] = []
      if (request.profile) {
        sets.push('Username = @username', 'FullName = @fullName', 'Email = @email')
        update.input('username', sql.NVarChar, request.profile.username)
          .input('fullName', sql.NVarChar, request.profile.fullName)
          .input('email', sql.NVarChar, request.profile.email)
      }
      if (request.isActive !== undefined) {
        sets.push('IsActive = @isActive')
        update.input('isActive', sql.Bit, request.isActive ? 1 : 0)
      }
      if (sets.length > 0) await update.query(`UPDATE ${T('Users')} SET ${sets.join(', ')} WHERE Id = @userId`)
      await advanceVersion(transaction, target.Id, request.expectedVersion)
    })
    return (await this.get(actor, objectId))!
  },

  async putOrganizationAccess(actor, objectId, organizationId, request) {
    await mutate(actor, objectId, 'put_organization_access', async transaction => {
      const authority = await readAuthority(transaction, actor)
      requireAuthority(authority, organizationId)
      const target = await requireTargetVersion(transaction, actor, objectId, request.expectedVersion)
      await updateProfile(transaction, target.Id, request.profile)
      await convergeMembership(transaction, actor, target, organizationId, request)
      await convergeDelegatedRoles(transaction, actor, target, organizationId, request)
      await advanceVersion(transaction, target.Id, request.expectedVersion)
    })
    return (await this.get(actor, objectId))!
  },

  async revokeRole(actor, objectId, organizationId, assignmentId, expectedVersion) {
    await mutate(actor, objectId, 'revoke_role', async transaction => {
      const authority = await readAuthority(transaction, actor)
      requireAuthority(authority, organizationId)
      const target = await requireTargetVersion(transaction, actor, objectId, expectedVersion)
      const result = await transaction.request().input('id', sql.NVarChar, assignmentId)
        .input('userId', sql.NVarChar, target.Id).input('organizationId', sql.NVarChar, organizationId)
        .input('updatedBy', sql.NVarChar, actor.objectId)
        .query(`UPDATE ${T('RoleAssignments')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy
          WHERE Id = @id AND UserId = @userId AND OrganizationId = @organizationId AND Source = 'delegated' AND Status = 'active'`)
      if ((result.rowsAffected?.[0] ?? 0) !== 1) {
        throw new EntraAccessError('not_found', 'The delegated role assignment was not found.', 404)
      }
      await advanceVersion(transaction, target.Id, expectedVersion)
    })
    return (await this.get(actor, objectId))!
  },
}

export const accessManagementRepo = Object.freeze(repository)