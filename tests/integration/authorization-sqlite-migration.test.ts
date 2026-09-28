import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import Database from 'better-sqlite3'
import { describe, expect, it } from 'vitest'

const schema = readFileSync(resolve(process.cwd(), 'server/storage/schema-sqlite.sql'), 'utf8')
const migration = readFileSync(resolve(process.cwd(), 'server/storage/db.ts'), 'utf8')

describe('SQLite authorization aggregate schema', () => {
  it('creates the explicit-default composite relationship and authorization version', () => {
    const db = new Database(':memory:')
    db.pragma('foreign_keys = ON')
    db.exec(schema)

    const userColumns = db.prepare('PRAGMA table_info(Users)').all() as Array<{ name: string }>
    const membershipColumns = db.prepare('PRAGMA table_info(OrganizationMemberships)').all() as Array<{ name: string }>
    const departmentKeys = db.prepare('PRAGMA index_list(DepartmentMemberships)').all() as Array<{ unique: number }>
    const defaultForeignKeys = db.prepare('PRAGMA foreign_key_list(OrganizationMemberships)').all() as Array<{ table: string; from: string }>

    expect(userColumns.map(column => column.name)).toContain('AuthorizationVersion')
    expect(membershipColumns.map(column => column.name)).toContain('DefaultDepartmentMembershipId')
    expect(departmentKeys.some(index => index.unique === 1)).toBe(true)
    expect(defaultForeignKeys.filter(key => key.table === 'DepartmentMemberships').map(key => key.from)).toEqual(
      expect.arrayContaining(['DefaultDepartmentMembershipId', 'UserId', 'OrganizationId']),
    )

    db.close()
  })

  it('defines a guarded table rebuild and deterministic default backfill', () => {
    expect(migration).toContain('ensureSqliteAuthorizationAggregateSchema')
    expect(migration).toMatch(/DepartmentMemberships_ExplicitDefaultUpgrade/)
    expect(migration).toMatch(/COUNT\(\*\).*<>\s*1/is)
    expect(migration).toMatch(/explicit default department/i)
  })

  it('retains revoked delegated assignment history behind an active-only unique index', () => {
    expect(schema).toMatch(
      /CREATE UNIQUE INDEX IF NOT EXISTS UX_RoleAssignments_ActiveDelegated[\s\S]*WHERE Status = 'active' AND Source = 'delegated'/i,
    )
    expect(schema).not.toMatch(/CREATE UNIQUE INDEX IF NOT EXISTS UX_RoleAssignments_Idempotency/)
  })
})