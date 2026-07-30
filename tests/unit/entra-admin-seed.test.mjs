import assert from 'node:assert/strict'
import { existsSync } from 'node:fs'
import { resolve } from 'node:path'
import { loadEnvFile } from 'node:process'
import { describe, it } from 'node:test'

import {
  buildBootstrapSql,
  validateBootstrapConfiguration,
} from '../../infra/scripts/seed-entra-admin-data.mjs'

const environmentFile = resolve(process.env.TALENTMATCH_ENV_FILE ?? '.env_qa_mcaps')
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
  for (const value of Object.values(configuration).filter(Boolean)) {
    assert.equal(sql.toLowerCase().includes(value.toLowerCase()), false)
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

  it('rejects unsupported execution modes', () => {
    assert.throws(() => buildBootstrapSql('delete'), /Unsupported bootstrap mode/)
  })
})