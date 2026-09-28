import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const schema = readFileSync(resolve(process.cwd(), 'server/storage/schema.sql'), 'utf8')
const initializer = readFileSync(resolve(process.cwd(), 'server/storage/db.ts'), 'utf8')
const stackBBootstrap = readFileSync(resolve(process.cwd(), 'dotnet/src/Web.Server/Program.cs'), 'utf8')
const columnUpgrades = readFileSync(
  resolve(process.cwd(), 'server/storage/schema-pre-batch-upgrades.sql'),
  'utf8',
)

const collectReconciledColumns = (source: string) => {
  const columns = new Map<string, Set<string>>()
  for (const match of source.matchAll(/COL_LENGTH\('talentmatch\.(\w+)',\s*'(\w+)'\)\s*IS NULL/g)) {
    const [, table, column] = match
    if (!columns.has(table)) columns.set(table, new Set())
    columns.get(table)!.add(column)
  }
  return columns
}

describe('Azure SQL authorization aggregate schema', () => {
  it('adds an optimistic authorization version to Entra users', () => {
    expect(schema).toMatch(/AuthorizationVersion\s+INT\s+NOT NULL\s+DEFAULT\s+0/i)
  })

  it('constrains an organization default to the same user and organization membership', () => {
    expect(schema).toMatch(/DefaultDepartmentMembershipId\s+NVARCHAR\(36\)\s+NULL/i)
    expect(schema).toMatch(/UNIQUE\s*\(Id,\s*UserId,\s*OrganizationId\)/i)
    expect(schema).toMatch(
      /FOREIGN KEY\s*\(DefaultDepartmentMembershipId,\s*UserId,\s*OrganizationId\)\s*REFERENCES\s*\[talentmatch\]\.DepartmentMemberships\s*\(Id,\s*UserId,\s*OrganizationId\)/i,
    )
  })

  it('requires active memberships to publish an explicit default', () => {
    expect(schema).toMatch(
      /Status\s*=\s*'active'\s+AND\s+RevokedAt\s+IS\s+NULL\s+AND\s+DefaultDepartmentMembershipId\s+IS\s+NOT\s+NULL/i,
    )
  })

  it('allows revoked assignment history while preventing equivalent active delegated assignments', () => {
    expect(schema).toMatch(
      /CREATE UNIQUE INDEX UX_RoleAssignments_ActiveDelegated[\s\S]*WHERE Status = 'active' AND Source = 'delegated'/i,
    )
    expect(schema).not.toMatch(/CREATE UNIQUE INDEX UX_RoleAssignments_Idempotency[\s\S]*?\);/i)
  })

  it('adds retro-fitted columns before the batches that reference them', () => {
    // Tables carry create-only guards, so a column added after a table's first release never
    // reaches an existing database. Any batch referencing such a column must therefore run
    // after that column has been reconciled, otherwise initialization aborts part-way.
    const reconciledBeforeLoop = collectReconciledColumns(columnUpgrades)
    const reconciledAnywhere = collectReconciledColumns(`${initializer}\n${columnUpgrades}`)

    const batches = schema
      .split(/\n(?=IF NOT EXISTS|CREATE (?:UNIQUE )?INDEX)/)
      .map(batch => batch.trim())
      .filter(batch => batch.length > 0 && !batch.startsWith('--'))

    const unresolved: string[] = []
    for (const batch of batches) {
      if (/^IF NOT EXISTS \(SELECT \* FROM sys\.tables/.test(batch)) continue
      const target = batch.match(/(?:ON|ALTER TABLE)\s+\[talentmatch\]\.(\w+)/)?.[1]
      if (!target) continue
      for (const column of reconciledAnywhere.get(target) ?? []) {
        if (!new RegExp(`\\b${column}\\b`).test(batch)) continue
        if (!reconciledBeforeLoop.get(target)?.has(column)) {
          unresolved.push(`${target}.${column}`)
        }
      }
    }

    expect(unresolved).toEqual([])
  })

  it('adopts pre-organization jobs so authorization cannot strand them', () => {
    const backfill = columnUpgrades.slice(columnUpgrades.indexOf('UPDATE j'))
    expect(backfill).toMatch(/SET j\.OrganizationId = o\.Id/i)
    expect(backfill).toMatch(/j\.DepartmentId = d\.Id/i)
    expect(backfill).toMatch(/WHERE j\.OrganizationId IS NULL OR j\.DepartmentId IS NULL/i)

    // The scope must come from the organization/department the job already names.
    expect(columnUpgrades).toMatch(
      /INNER JOIN \[talentmatch\]\.Organizations o\s*\n\s*ON o\.Status = 'active' AND o\.Name = LTRIM\(RTRIM\(j\.Organisation\)\)/i,
    )
    expect(columnUpgrades).toMatch(
      /INNER JOIN \[talentmatch\]\.Departments d\s*\n\s*ON d\.Status = 'active' AND d\.OrganizationId = o\.Id AND d\.Name = LTRIM\(RTRIM\(j\.Department\)\)/i,
    )

    // Both scope columns must already exist, otherwise the backfill aborts the whole bootstrap.
    const guard = columnUpgrades.slice(0, columnUpgrades.indexOf('@backfillActor'))
    expect(guard).toMatch(/COL_LENGTH\('talentmatch\.Jobs', 'OrganizationId'\) IS NOT NULL/i)
    expect(guard).toMatch(/COL_LENGTH\('talentmatch\.Jobs', 'DepartmentId'\) IS NOT NULL/i)
  })

  it('runs the column upgrades before the schema batches in both stack bootstraps', () => {
    const stackAUpgrades = initializer.indexOf('schema-pre-batch-upgrades.sql')
    const stackALoop = initializer.indexOf('for (const batch of batches)')
    expect(stackAUpgrades).toBeGreaterThan(-1)
    expect(stackAUpgrades).toBeLessThan(stackALoop)

    const stackBUpgrades = stackBBootstrap.indexOf('foreach (var upgrade in preBatchUpgrades)')
    const stackBLoop = stackBBootstrap.indexOf('foreach (var batch in batches)')
    expect(stackBUpgrades).toBeGreaterThan(-1)
    expect(stackBUpgrades).toBeLessThan(stackBLoop)
  })

  it('creates the candidate key the default-department foreign key resolves against', () => {
    // The foreign key batch is not skipped on an existing database, but the unique constraint it
    // targets lives inside a create-only CREATE TABLE guard, so it must be reconciled up front.
    expect(columnUpgrades).toMatch(
      /ADD CONSTRAINT UQ_DepartmentMemberships_Id_User_Organization UNIQUE \(Id, UserId, OrganizationId\)/i,
    )
  })

  it('shares every retro-fitted Users column with the stack that owns the Azure SQL bootstrap', () => {
    // Stack B owns the Azure SQL schema, and its bootstrap has no Users reconciliation of its own,
    // so anything Stack A back-fills must live in the shared file both stacks execute.
    const stackAUsersColumns = [
      ...initializer.matchAll(/ALTER TABLE \[talentmatch\]\.Users ADD (\w+)/g),
    ].map(([, column]) => column)

    expect(stackAUsersColumns.length).toBeGreaterThan(0)
    const sharedColumns = new Set(
      [
        ...columnUpgrades
          .replace(/\s+/g, ' ')
          .matchAll(/ALTER TABLE \[talentmatch\]\.Users ADD (\w+)/g),
      ].map(([, column]) => column),
    )
    const missing = stackAUsersColumns.filter(column => !sharedColumns.has(column))
    expect(missing).toEqual([])
  })
})