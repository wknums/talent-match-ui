import { getPool, sql } from '../db.js'
import { T } from '../table-names.js'
import type { Department, DepartmentMembership, Organization, OrganizationMembership } from '../../../src/types/index.js'
import type { AuthorizationMembership } from '../../services/authorization.js'

const asIso = (value: any) => value?.toISOString?.() ?? value

export const organizationRepo = {
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
          d.Id AS DepartmentId, d.Name AS DepartmentName
        FROM ${T('OrganizationMemberships')} om
        INNER JOIN ${T('Organizations')} o ON o.Id = om.OrganizationId AND o.Status = 'active'
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

  async activateMemberships(organizationMembership: OrganizationMembership, departmentMembership: DepartmentMembership): Promise<void> {
    if (organizationMembership.userId !== departmentMembership.userId || organizationMembership.organizationId !== departmentMembership.organizationId) throw new Error('Membership scopes must agree.')
    const pool = await getPool()
    const transaction = pool.transaction()
    await transaction.begin()
    try {
      const department = await transaction.request().input('id', sql.NVarChar, departmentMembership.departmentId).input('organizationId', sql.NVarChar, departmentMembership.organizationId)
        .query(`SELECT Id FROM ${T('Departments')} WHERE Id = @id AND OrganizationId = @organizationId AND Status = 'active'`)
      if (!department.recordset[0]) throw new Error('Department does not belong to the active organization.')
      await transaction.request().input('id', sql.NVarChar, organizationMembership.membershipId).input('userId', sql.NVarChar, organizationMembership.userId).input('organizationId', sql.NVarChar, organizationMembership.organizationId).input('updatedBy', sql.NVarChar, organizationMembership.updatedBy)
        .query(`INSERT INTO ${T('OrganizationMemberships')} (Id, UserId, OrganizationId, Status, EffectiveAt, UpdatedBy) VALUES (@id, @userId, @organizationId, 'active', SYSUTCDATETIME(), @updatedBy)`)
      await transaction.request().input('id', sql.NVarChar, departmentMembership.membershipId).input('userId', sql.NVarChar, departmentMembership.userId).input('organizationId', sql.NVarChar, departmentMembership.organizationId).input('departmentId', sql.NVarChar, departmentMembership.departmentId).input('updatedBy', sql.NVarChar, departmentMembership.updatedBy)
        .query(`INSERT INTO ${T('DepartmentMemberships')} (Id, UserId, OrganizationId, DepartmentId, Status, EffectiveAt, UpdatedBy) VALUES (@id, @userId, @organizationId, @departmentId, 'active', SYSUTCDATETIME(), @updatedBy)`)
      await transaction.commit()
    } catch (error) { await transaction.rollback(); throw error }
  },
}