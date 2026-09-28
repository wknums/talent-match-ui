import assert from 'node:assert/strict'
import { existsSync } from 'node:fs'
import { resolve } from 'node:path'
import { loadEnvFile } from 'node:process'
import { describe, it } from 'node:test'

import {
  buildBootstrapSql,
  validateBootstrapConfiguration,
} from '../../infra/scripts/seed-entra-admin-data.mjs'

const environmentFile = resolve(process.env.TALENTMATCH_ENV_FILE ?? '.env_qa')
if (existsSync(environmentFile)) loadEnvFile(environmentFile)

const configuration = {
  tenantId: process.env.AZURE_TENANT_ID,
  objectId: process.env.ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID,
  username: process.env.ENTRA_BOOTSTRAP_ADMIN_USERNAME,
  fullName: process.env.ENTRA_BOOTSTRAP_ADMIN_FULL_NAME,
  email: process.env.ENTRA_BOOTSTRAP_ADMIN_EMAIL,
  organizationName: process.env.ENTRA_BOOTSTRAP_ORGANIZATION_NAME,
  departmentName: process.env.ENTRA_BOOTSTRAP_DEPARTMENT_NAME,
}

function assertConfigurationIsParameterized(sql) {
  // Interpolated configuration would surface as a quoted SQL literal. Matching on the bare value
  // instead would flag any short name that happens to be a substring of the surrounding T-SQL.
  const literals = [...sql.matchAll(/'((?:[^']|'')*)'/g)].map(([, literal]) =>
    literal.toLowerCase(),
  )
  for (const value of Object.values(configuration).filter(Boolean)) {
    assert.equal(literals.includes(value.toLowerCase()), false)
  }
}

describe('Entra bootstrap admin data seed', () => {
  it('rejects incomplete identity and scope configuration', () => {
    assert.throws(
      () => validateBootstrapConfiguration({ ...configuration, departmentName: '' }),
      /departmentName/,
    )
  })

  it('keeps check mode read-only and parameterized', () => {
    const sql = buildBootstrapSql('check')

    assert.doesNotMatch(sql, /\b(?:INSERT|UPDATE|DELETE|MERGE)\b/i)
    assert.match(sql, /@tenantId/)
    assert.match(sql, /@objectId/)
    assert.match(sql, /@organizationName/)
    assert.match(sql, /@departmentName/)
    assertConfigurationIsParameterized(sql)
  })

  it('applies every authorization record in one idempotent transaction', () => {
    const sql = buildBootstrapSql('apply')

    assert.match(sql, /SET XACT_ABORT ON/)
    assert.match(sql, /BEGIN TRANSACTION/)
    assert.match(sql, /COMMIT TRANSACTION/)
    assert.match(sql, /\[talentmatch\]\.\[Users\]/)
    assert.match(sql, /\[talentmatch\]\.\[Organizations\]/)
    assert.match(sql, /\[talentmatch\]\.\[Departments\]/)
    assert.match(sql, /\[talentmatch\]\.\[OrganizationMemberships\]/)
    assert.match(sql, /\[talentmatch\]\.\[DepartmentMemberships\]/)
    assert.match(sql, /\[talentmatch\]\.\[RoleAssignments\]/)
    assert.match(sql, /\[talentmatch\]\.\[ProcessingEvents\]/)
    assert.match(sql, /Source = 'bootstrap'/)
    assert.match(sql, /Role = 'admin'/)
    assert.match(sql, /IF @userId IS NULL/i)
    assert.match(sql, /IF @organizationId IS NULL/i)
    assert.match(sql, /IF @departmentId IS NULL/i)
    assertConfigurationIsParameterized(sql)
  })

  it('creates the department membership before assigning the explicit organization default', () => {
    const sql = buildBootstrapSql('apply')
    const departmentInsert = sql.indexOf('INSERT INTO [talentmatch].[DepartmentMemberships]')
    const organizationInsert = sql.indexOf('INSERT INTO [talentmatch].[OrganizationMemberships]')

    assert.notEqual(departmentInsert, -1)
    assert.notEqual(organizationInsert, -1)
    assert.ok(departmentInsert < organizationInsert)
    assert.match(sql, /OrganizationMemberships[\s\S]*DefaultDepartmentMembershipId/i)
    assert.match(sql, /SET[\s\S]*DefaultDepartmentMembershipId\s*=\s*@departmentMembershipId/i)
  })

  it('converges an existing membership whose default predates the column', () => {
    const sql = buildBootstrapSql('apply')
    const guard = sql.slice(
      sql.indexOf('ELSE IF EXISTS', sql.indexOf('INSERT INTO [talentmatch].[OrganizationMemberships]')),
    )

    // "DefaultDepartmentMembershipId <> @departmentMembershipId" evaluates to UNKNOWN against NULL,
    // so a NULL default would slip past the drift guard and fail the 51004 postcondition instead.
    assert.match(guard.slice(0, guard.indexOf('BEGIN')), /DefaultDepartmentMembershipId IS NULL/i)
  })

  it('serializes concurrent applies and advances authorization version once per change', () => {
    const sql = buildBootstrapSql('apply')

    assert.match(sql, /sp_getapplock/i)
    assert.match(sql, /@LockOwner\s*=\s*'Transaction'/i)
    assert.match(sql, /AuthorizationVersion\s*=\s*AuthorizationVersion\s*\+\s*1/i)
    assert.match(sql, /IF @changed = 1/i)
  })

  it('rolls back every bootstrap mutation when any postcondition fails', () => {
    const sql = buildBootstrapSql('apply')

    assert.match(sql, /THROW 510\d{2}/i)
    assert.match(sql, /IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION/i)
    assert.ok(sql.indexOf('COMMIT TRANSACTION') > sql.lastIndexOf('THROW 510'))
  })

  it('rejects unsupported execution modes', () => {
    assert.throws(() => buildBootstrapSql('delete'), /Unsupported bootstrap mode/)
  })
})