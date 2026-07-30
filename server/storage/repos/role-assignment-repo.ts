import { getPool, sql } from '../db.js'
import { T } from '../table-names.js'
import type { RoleAssignment, RoleGroupMapping } from '../../../src/types/index.js'

function rowToGroupMapping(row: any): RoleGroupMapping {
  return {
    mappingId: row.Id,
    tenantId: row.TenantId,
    groupObjectId: row.GroupObjectId,
    role: row.Role,
    organizationId: row.OrganizationId ?? undefined,
    departmentId: row.DepartmentId ?? undefined,
    enabled: row.Enabled === true || row.Enabled === 1,
    createdAt: row.CreatedAt?.toISOString?.() ?? row.CreatedAt,
    updatedAt: row.UpdatedAt?.toISOString?.() ?? row.UpdatedAt,
    updatedBy: row.UpdatedBy,
  }
}

export const roleAssignmentRepo = {
  async upsertGroupMapping(mapping: RoleGroupMapping): Promise<void> {
    const pool = await getPool()
    const existing = await pool.request().input('tenantId', sql.NVarChar, mapping.tenantId).input('groupId', sql.NVarChar, mapping.groupObjectId)
      .query(`SELECT Id FROM ${T('RoleGroupMappings')} WHERE TenantId = @tenantId AND GroupObjectId = @groupId`)
    const request = pool.request().input('id', sql.NVarChar, mapping.mappingId).input('tenantId', sql.NVarChar, mapping.tenantId).input('groupId', sql.NVarChar, mapping.groupObjectId).input('role', sql.NVarChar, mapping.role).input('organizationId', sql.NVarChar, mapping.organizationId ?? null).input('departmentId', sql.NVarChar, mapping.departmentId ?? null).input('enabled', sql.Bit, mapping.enabled ? 1 : 0).input('updatedBy', sql.NVarChar, mapping.updatedBy)
    if (existing.recordset[0]) {
      await request.query(`UPDATE ${T('RoleGroupMappings')} SET Role = @role, OrganizationId = @organizationId, DepartmentId = @departmentId, Enabled = @enabled, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy WHERE TenantId = @tenantId AND GroupObjectId = @groupId`)
    } else {
      await request.query(`INSERT INTO ${T('RoleGroupMappings')} (Id, TenantId, GroupObjectId, Role, OrganizationId, DepartmentId, Enabled, CreatedAt, UpdatedAt, UpdatedBy) VALUES (@id, @tenantId, @groupId, @role, @organizationId, @departmentId, @enabled, SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
    }
  },

  async getActiveForIdentity(tenantId: string, objectId: string): Promise<RoleAssignment[]> {
    const pool = await getPool()
    const result = await pool.request().input('tenantId', sql.NVarChar, tenantId).input('objectId', sql.NVarChar, objectId)
      .query(`SELECT * FROM ${T('RoleAssignments')} WHERE TenantId = @tenantId AND UserObjectId = @objectId AND Status = 'active' ORDER BY EffectiveAt`)
    return result.recordset.map((row: any) => ({
      assignmentId: row.Id, userId: row.UserId, tenantId: row.TenantId, userObjectId: row.UserObjectId,
      role: row.Role, organizationId: row.OrganizationId ?? undefined, departmentId: row.DepartmentId ?? undefined,
      roleGroupMappingId: row.RoleGroupMappingId ?? undefined, source: row.Source, status: row.Status,
      effectiveAt: row.EffectiveAt?.toISOString?.() ?? row.EffectiveAt, revokedAt: row.RevokedAt?.toISOString?.() ?? row.RevokedAt ?? undefined,
      createdAt: row.CreatedAt?.toISOString?.() ?? row.CreatedAt, updatedAt: row.UpdatedAt?.toISOString?.() ?? row.UpdatedAt, updatedBy: row.UpdatedBy,
    }))
  },

  async getEnabledGroupMappings(tenantId: string, groupObjectIds: string[]): Promise<RoleGroupMapping[]> {
    if (groupObjectIds.length === 0) return []
    const pool = await getPool()
    const request = pool.request().input('tenantId', sql.NVarChar, tenantId)
    const placeholders = groupObjectIds.map((id, index) => { request.input(`group${index}`, sql.NVarChar, id); return `@group${index}` })
    const result = await request.query(`SELECT * FROM ${T('RoleGroupMappings')} WHERE TenantId = @tenantId AND Enabled = 1 AND GroupObjectId IN (${placeholders.join(', ')})`)
    return result.recordset.map(rowToGroupMapping)
  },

  async getGroupMappingsByIds(mappingIds: string[]): Promise<RoleGroupMapping[]> {
    if (mappingIds.length === 0) return []
    const pool = await getPool()
    const request = pool.request()
    const placeholders = mappingIds.map((id, index) => {
      request.input(`mapping${index}`, sql.NVarChar, id)
      return `@mapping${index}`
    })
    const result = await request.query(
      `SELECT * FROM ${T('RoleGroupMappings')} WHERE Id IN (${placeholders.join(', ')})`,
    )
    return result.recordset.map(rowToGroupMapping)
  },

  async activate(assignment: RoleAssignment): Promise<void> {
    const pool = await getPool()
    await pool.request().input('id', sql.NVarChar, assignment.assignmentId).input('userId', sql.NVarChar, assignment.userId).input('tenantId', sql.NVarChar, assignment.tenantId).input('objectId', sql.NVarChar, assignment.userObjectId).input('role', sql.NVarChar, assignment.role).input('organizationId', sql.NVarChar, assignment.organizationId ?? null).input('departmentId', sql.NVarChar, assignment.departmentId ?? null).input('mappingId', sql.NVarChar, assignment.roleGroupMappingId ?? null).input('source', sql.NVarChar, assignment.source).input('updatedBy', sql.NVarChar, assignment.updatedBy)
      .query(`INSERT INTO ${T('RoleAssignments')} (Id, UserId, TenantId, UserObjectId, Role, OrganizationId, DepartmentId, RoleGroupMappingId, Source, Status, EffectiveAt, CreatedAt, UpdatedAt, UpdatedBy) VALUES (@id, @userId, @tenantId, @objectId, @role, @organizationId, @departmentId, @mappingId, @source, 'active', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), @updatedBy)`)
  },

  async revoke(assignmentId: string, updatedBy: string): Promise<void> {
    const pool = await getPool()
    await pool.request().input('id', sql.NVarChar, assignmentId).input('updatedBy', sql.NVarChar, updatedBy)
      .query(`UPDATE ${T('RoleAssignments')} SET Status = 'revoked', RevokedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy WHERE Id = @id AND Status = 'active'`)
  },
}