import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import type { AccessManagementActor } from '../../server/services/entra-access-management.js'

const tenantId = '10000000-0000-4000-8000-000000000001'
const actorObjectId = '10000000-0000-4000-8000-000000000002'
const actorUserId = '10000000-0000-4000-8000-000000000003'
const targetObjectId = '20000000-0000-4000-8000-000000000001'
const targetUserId = '20000000-0000-4000-8000-000000000002'
const organizationId = '30000000-0000-4000-8000-000000000001'
const otherOrganizationId = '30000000-0000-4000-8000-000000000002'
const departmentAId = '40000000-0000-4000-8000-000000000001'
const departmentBId = '40000000-0000-4000-8000-000000000002'
const otherDepartmentId = '40000000-0000-4000-8000-000000000003'
const temporaryDirectory = mkdtempSync(join(tmpdir(), 'talentmatch-access-repo-'))

let pool: any
let closePool: () => Promise<void>
let repository: typeof import('../../server/storage/repos/access-management-repo.js').accessManagementRepo

const actor = (overrides: Partial<AccessManagementActor> = {}): AccessManagementActor => ({
  tenantId,
  objectId: actorObjectId,
  globalAdmin: true,
  organizationAdminIds: [],
  correlationId: '50000000-0000-4000-8000-000000000001',
  ...overrides,
})

async function query(sqlText: string) {
  const statements = sqlText.split(';').map(statement => statement.trim()).filter(Boolean)
  let result: any
  for (const statement of statements) result = await pool.request().query(statement)
  return result
}

async function seed() {
  await query(`
    INSERT INTO Users (Id, Username, Role, FullName, Email, CreatedAt, AuthenticationProvider, EntraTenantId, EntraObjectId, IsActive, AuthorizationVersion)
    VALUES ('${actorUserId}', 'admin@example.com', 'admin', 'Admin', 'admin@example.com', datetime('now'), 'entra', '${tenantId}', '${actorObjectId}', 1, 0),
           ('${targetUserId}', 'target@example.com', 'recruiter', 'Target', 'target@example.com', datetime('now'), 'entra', '${tenantId}', '${targetObjectId}', 1, 0);
    INSERT INTO Organizations (Id, Name, Status, UpdatedBy)
    VALUES ('${organizationId}', 'Primary', 'active', 'seed'),
           ('${otherOrganizationId}', 'Other', 'active', 'seed');
    INSERT INTO Departments (Id, OrganizationId, Name, Status, UpdatedBy)
    VALUES ('${departmentAId}', '${organizationId}', 'A', 'active', 'seed'),
           ('${departmentBId}', '${organizationId}', 'B', 'active', 'seed'),
           ('${otherDepartmentId}', '${otherOrganizationId}', 'Other', 'active', 'seed');
    INSERT INTO RoleAssignments (Id, UserId, TenantId, UserObjectId, Role, Source, Status, UpdatedBy)
    VALUES ('60000000-0000-4000-8000-000000000001', '${actorUserId}', '${tenantId}', '${actorObjectId}', 'admin', 'bootstrap', 'active', 'seed');
  `)
}

function request(expectedVersion = 0) {
  return {
    expectedVersion,
    profile: { username: 'target@example.com', fullName: 'Target User', email: 'target@example.com' },
    membership: {
      status: 'active' as const,
      departmentIds: [departmentAId, departmentBId],
      defaultDepartmentId: departmentAId,
    },
    roleAssignments: [{ role: 'recruiter' as const, departmentId: departmentAId }],
  }
}

beforeAll(async () => {
  process.env.STORAGE_PROVIDER = 'local'
  process.env.SQLITE_DB_PATH = join(temporaryDirectory, 'access.db')
  const database = await import('../../server/storage/db.js')
  await database.initializeDatabase()
  pool = await database.getPool()
  closePool = () => pool.close()
  repository = (await import('../../server/storage/repos/access-management-repo.js')).accessManagementRepo
})

beforeEach(async () => {
  await query(`
    DELETE FROM ProcessingEvents;
    DELETE FROM RoleAssignments;
    DELETE FROM RoleGroupMappings;
    DELETE FROM OrganizationMemberships;
    DELETE FROM DepartmentMemberships;
    DELETE FROM Departments;
    DELETE FROM Organizations;
    DELETE FROM Users;
  `)
  await seed()
})

afterAll(async () => {
  await closePool()
  rmSync(temporaryDirectory, { recursive: true, force: true })
})

describe('Stack A Entra access-management repository', () => {
  it('atomically onboards an existing pending profile with an explicit default and success audit', async () => {
    const aggregate = await repository.putOrganizationAccess(actor(), targetObjectId, organizationId, request())

    expect(aggregate.authorizationVersion).toBe(1)
    expect(aggregate.organizations).toContainEqual(expect.objectContaining({
      organizationId,
      departmentIds: [departmentAId, departmentBId],
      defaultDepartmentId: departmentAId,
    }))
    const result = await query(`
      SELECT
        (SELECT COUNT(*) FROM OrganizationMemberships WHERE UserId = '${targetUserId}' AND Status = 'active') AS Organizations,
        (SELECT COUNT(*) FROM DepartmentMemberships WHERE UserId = '${targetUserId}' AND Status = 'active') AS Departments,
        (SELECT COUNT(*) FROM RoleAssignments WHERE UserId = '${targetUserId}' AND Source = 'delegated' AND Status = 'active') AS Roles,
        (SELECT COUNT(*) FROM ProcessingEvents WHERE Action = 'access_management_succeeded') AS Audits
    `)
    expect(result.recordset[0]).toMatchObject({ Organizations: 1, Departments: 2, Roles: 1, Audits: 1 })
  })

  it('rolls back stale mutations and records one failure outcome after rollback', async () => {
    await repository.putOrganizationAccess(actor(), targetObjectId, organizationId, request())

    await expect(repository.putOrganizationAccess(actor(), targetObjectId, organizationId, {
      ...request(0),
      profile: { username: 'changed@example.com', fullName: 'Changed', email: null },
    })).rejects.toMatchObject({ code: 'version_conflict', statusCode: 409 })

    const result = await query(`SELECT Username, AuthorizationVersion FROM Users WHERE Id = '${targetUserId}'`)
    expect(result.recordset[0]).toMatchObject({ Username: 'target@example.com', AuthorizationVersion: 1 })
    const audits = await query(`SELECT Action FROM ProcessingEvents ORDER BY Timestamp`)
    expect(audits.recordset.map((row: any) => row.Action)).toEqual([
      'access_management_succeeded',
      'access_management_failed',
    ])
  })

  it('revalidates actor authority inside the transaction', async () => {
    await query(`UPDATE RoleAssignments SET Status = 'revoked', RevokedAt = datetime('now') WHERE UserObjectId = '${actorObjectId}'`)

    await expect(repository.putOrganizationAccess(actor(), targetObjectId, organizationId, request()))
      .rejects.toMatchObject({ code: 'forbidden', statusCode: 403 })

    const result = await query(`SELECT AuthorizationVersion FROM Users WHERE Id = '${targetUserId}'`)
    expect(result.recordset[0].AuthorizationVersion).toBe(0)
  })

  it('replaces only the requested scope while preserving unrelated and group assignments', async () => {
    await repository.putOrganizationAccess(actor(), targetObjectId, otherOrganizationId, {
      ...request(),
      membership: { status: 'active', departmentIds: [otherDepartmentId], defaultDepartmentId: otherDepartmentId },
      roleAssignments: [{ role: 'business_panel', departmentId: otherDepartmentId }],
    })
    await query(`
      INSERT INTO RoleGroupMappings (Id, TenantId, GroupObjectId, Role, OrganizationId, DepartmentId, Enabled, UpdatedBy)
      VALUES ('70000000-0000-4000-8000-000000000001', '${tenantId}', '70000000-0000-4000-8000-000000000002', 'business_panel', '${organizationId}', '${departmentBId}', 1, 'seed');
      INSERT INTO RoleAssignments (Id, UserId, TenantId, UserObjectId, Role, OrganizationId, DepartmentId, RoleGroupMappingId, Source, Status, UpdatedBy)
      VALUES ('70000000-0000-4000-8000-000000000003', '${targetUserId}', '${tenantId}', '${targetObjectId}', 'business_panel', '${organizationId}', '${departmentBId}', '70000000-0000-4000-8000-000000000001', 'group', 'active', 'seed');
    `)

    const aggregate = await repository.putOrganizationAccess(actor(), targetObjectId, organizationId, request(1))

    expect(aggregate.authorizationVersion).toBe(2)
    expect(aggregate.organizations.map(item => item.organizationId)).toEqual([organizationId, otherOrganizationId])
    const preserved = await query(`SELECT COUNT(*) AS Count FROM RoleAssignments WHERE UserId = '${targetUserId}' AND Source = 'group' AND Status = 'active'`)
    expect(preserved.recordset[0].Count).toBe(1)
  })

  it('revokes only the requested delegated assignment', async () => {
    const aggregate = await repository.putOrganizationAccess(actor(), targetObjectId, organizationId, request())
    const assignmentId = aggregate.organizations[0].roleAssignments.find(item => item.source === 'delegated')!.id

    const updated = await repository.revokeRole(actor(), targetObjectId, organizationId, assignmentId, 1)

    expect(updated.authorizationVersion).toBe(2)
    expect(updated.organizations[0].roleAssignments).toEqual([])
  })

  it('allows only one of six concurrent onboarding requests to advance the expected version', async () => {
    const results = await Promise.allSettled(Array.from({ length: 6 }, () =>
      repository.putOrganizationAccess(actor(), targetObjectId, organizationId, request())))

    expect(results.filter(result => result.status === 'fulfilled')).toHaveLength(1)
    expect(results.filter(result => result.status === 'rejected')).toHaveLength(5)
    for (const result of results) {
      if (result.status === 'rejected') {
        expect(result.reason).toMatchObject({ code: 'version_conflict', statusCode: 409 })
      }
    }
    const state = await query(`
      SELECT
        (SELECT AuthorizationVersion FROM Users WHERE Id = '${targetUserId}') AS Version,
        (SELECT COUNT(*) FROM OrganizationMemberships WHERE UserId = '${targetUserId}' AND Status = 'active') AS Organizations,
        (SELECT COUNT(*) FROM DepartmentMemberships WHERE UserId = '${targetUserId}' AND Status = 'active') AS Departments,
        (SELECT COUNT(*) FROM RoleAssignments WHERE UserId = '${targetUserId}' AND Source = 'delegated' AND Status = 'active') AS Roles
    `)
    expect(state.recordset[0]).toMatchObject({ Version: 1, Organizations: 1, Departments: 2, Roles: 1 })
  })

  it('uses the ordered user ID as the cursor without repeating a profile', async () => {
    const firstPage = await repository.list(actor(), { limit: 1 })
    const secondPage = await repository.list(actor(), { limit: 1, cursor: firstPage.nextCursor! })

    expect(firstPage.nextCursor).not.toBeNull()
    expect(secondPage.items[0].objectId).not.toBe(firstPage.items[0].objectId)
  })

  it('scopes an unfiltered Organization Admin list to assigned organizations and pending profiles', async () => {
    const outsideObjectId = '20000000-0000-4000-8000-000000000010'
    const outsideUserId = '20000000-0000-4000-8000-000000000011'
    await repository.putOrganizationAccess(actor(), targetObjectId, organizationId, request())
    await repository.putOrganizationAccess(actor(), targetObjectId, otherOrganizationId, {
      ...request(1),
      membership: { status: 'active', departmentIds: [otherDepartmentId], defaultDepartmentId: otherDepartmentId },
      roleAssignments: [{ role: 'business_panel', departmentId: otherDepartmentId }],
    })
    await query(`
      INSERT INTO Users (Id, Username, Role, FullName, Email, CreatedAt, AuthenticationProvider, EntraTenantId, EntraObjectId, IsActive, AuthorizationVersion)
      VALUES ('${outsideUserId}', 'outside@example.com', 'recruiter', 'Outside User', 'outside@example.com', datetime('now'), 'entra', '${tenantId}', '${outsideObjectId}', 1, 0);
      INSERT INTO DepartmentMemberships (Id, UserId, OrganizationId, DepartmentId, Status, EffectiveAt, UpdatedBy)
      VALUES ('80000000-0000-4000-8000-000000000001', '${actorUserId}', '${organizationId}', '${departmentAId}', 'active', datetime('now'), 'seed'),
             ('80000000-0000-4000-8000-000000000002', '${outsideUserId}', '${otherOrganizationId}', '${otherDepartmentId}', 'active', datetime('now'), 'seed');
      INSERT INTO OrganizationMemberships (Id, UserId, OrganizationId, DefaultDepartmentMembershipId, Status, EffectiveAt, UpdatedBy)
      VALUES ('80000000-0000-4000-8000-000000000003', '${actorUserId}', '${organizationId}', '80000000-0000-4000-8000-000000000001', 'active', datetime('now'), 'seed'),
             ('80000000-0000-4000-8000-000000000004', '${outsideUserId}', '${otherOrganizationId}', '80000000-0000-4000-8000-000000000002', 'active', datetime('now'), 'seed');
      UPDATE RoleAssignments SET Status = 'revoked', RevokedAt = datetime('now') WHERE UserObjectId = '${actorObjectId}';
      INSERT INTO RoleAssignments (Id, UserId, TenantId, UserObjectId, Role, OrganizationId, Source, Status, UpdatedBy)
      VALUES ('80000000-0000-4000-8000-000000000005', '${actorUserId}', '${tenantId}', '${actorObjectId}', 'organization_admin', '${organizationId}', 'delegated', 'active', 'seed');
    `)

    const page = await repository.list(actor({ globalAdmin: false, organizationAdminIds: [organizationId] }), { limit: 50 })
    const detail = await repository.get(
      actor({ globalAdmin: false, organizationAdminIds: [organizationId] }),
      targetObjectId,
    )

    expect(page.items.map(item => item.objectId)).toContain(targetObjectId)
    expect(page.items.map(item => item.objectId)).not.toContain(outsideObjectId)
    expect(page.items.find(item => item.objectId === targetObjectId)?.organizations.map(item => item.organizationId))
      .toEqual([organizationId])
    expect(detail?.organizations.map(item => item.organizationId)).toEqual([organizationId])
  })
})