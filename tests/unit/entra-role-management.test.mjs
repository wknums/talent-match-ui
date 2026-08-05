import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import {
  chmodSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { delimiter, join, resolve } from 'node:path'
import { afterEach, describe, it } from 'node:test'

import {
  buildRoleManagementSql,
  validateRoleManagementConfiguration,
  validateRoleScope,
} from '../../infra/scripts/manage-entra-role-data.mjs'

const TENANT_ID = '11111111-1111-1111-1111-111111111111'
const SUBSCRIPTION_ID = '22222222-2222-2222-2222-222222222222'
const API_SERVICE_PRINCIPAL_ID = '33333333-3333-3333-3333-333333333333'
const RECRUITER_ROLE_ID = '44444444-4444-4444-4444-444444444444'
const GROUP_ID = '55555555-5555-5555-5555-555555555555'
const ORGANIZATION_ID = '66666666-6666-6666-6666-666666666666'
const DEPARTMENT_ID = '77777777-7777-7777-7777-777777777777'
const USER_ID = '88888888-8888-8888-8888-888888888888'
const MAPPING_ID = '99999999-9999-9999-9999-999999999999'
const ASSIGNMENT_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
const temporaryDirectories = []

afterEach(() => {
  for (const directory of temporaryDirectories.splice(0)) {
    rmSync(directory, { recursive: true, force: true })
  }
})

function assertParameterized(sql, values) {
  for (const value of values) {
    assert.equal(sql.toLowerCase().includes(value.toLowerCase()), false)
  }
}

function createCliFixture(options = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'talentmatch-entra-role-'))
  temporaryDirectories.push(directory)
  const commandLog = join(directory, 'commands.log')
  const environmentFile = join(directory, '.env_test')
  const azPath = join(directory, 'az')
  const dataHelperPath = join(directory, 'role-data.mjs')
  const membershipState = join(directory, 'membership-state.txt')

  writeFileSync(commandLog, '')
  writeFileSync(membershipState, String(options.memberCount ?? 1))

  writeFileSync(environmentFile, [
    `AZURE_TENANT_ID=${TENANT_ID}`,
    `AZURE_SUBSCRIPTION_ID=${SUBSCRIPTION_ID}`,
    'SQL_RG=rg-test',
    'SQL_SERVER_NAME=sql-test',
    'SQL_DATABASE_NAME=talentmatch-test',
    `ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID=${API_SERVICE_PRINCIPAL_ID}`,
    'ENTRA_ADMIN_APP_ROLE_ID=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    'ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID=cccccccc-cccc-cccc-cccc-cccccccccccc',
    `ENTRA_RECRUITER_APP_ROLE_ID=${RECRUITER_ROLE_ID}`,
    'ENTRA_BUSINESS_PANEL_APP_ROLE_ID=dddddddd-dddd-dddd-dddd-dddddddddddd',
    'OPERATOR_TEST_SECRET=must-not-appear',
    '',
  ].join('\n'))

  writeFileSync(azPath, `#!/usr/bin/env bash
printf 'az %s\\n' "$*" >> "$MOCK_COMMAND_LOG"
if [[ "$*" == *"account show"*"--query tenantId"* ]]; then
  printf '%s\\r\\n' "$MOCK_ACTIVE_TENANT"
elif [[ "$*" == *"account show"*"--query id"* ]]; then
  printf '%s\\r\\n' "$MOCK_ACTIVE_SUBSCRIPTION"
elif [[ "$*" == *"account get-access-token"* ]]; then
  printf 'mock-sql-access-token\\r\\n'
elif [[ "$*" == *"ad group show"* ]]; then
  printf '{"id":"%s","securityEnabled":%s,"groupTypes":[]}\\r\\n' "$MOCK_GROUP_ID" "$MOCK_GROUP_SECURITY_ENABLED"
elif [[ "$*" == *"ad user show"* ]]; then
  printf '{"id":"%s","accountEnabled":true,"userType":"Member","userPrincipalName":"operator.user@example.test","displayName":"Operator User","mail":"operator.user@example.test"}\\r\\n' "$MOCK_USER_ID"
elif [[ "$*" == *"ad sp show"* ]]; then
  printf '{"id":"%s","value":"recruiter","isEnabled":true}\\r\\n' "$MOCK_ROLE_ID"
elif [[ "$*" == *"appRoleAssignedTo"* ]]; then
  printf '1\\r\\n'
elif [[ "$*" == *"memberOf"* ]]; then
  printf '[{"id":"%s","securityEnabled":true}]\\r\\n' "$MOCK_GROUP_ID"
elif [[ "$*" == *"appRoleAssignments"* ]]; then
  printf '[{"resourceId":"%s","appRoleId":"%s"}]\\r\\n' "$MOCK_API_SP_ID" "$MOCK_ROLE_ID"
elif [[ "$*" == *"members"* && "$*" == *"--method get"* ]]; then
  cat "$MOCK_MEMBERSHIP_STATE"
  printf '\\r\\n'
elif [[ "$*" == *"--method post"* || "$*" == *"--method delete"* ]]; then
  [[ "$MOCK_FAIL_GRAPH_WRITE" == "true" ]] && exit 65
  if [[ "$*" == *"/members/"* || "$*" == *"/members?"* ]]; then
    [[ "$*" == *"--method post"* ]] && printf '1' > "$MOCK_MEMBERSHIP_STATE"
    [[ "$*" == *"--method delete"* ]] && printf '0' > "$MOCK_MEMBERSHIP_STATE"
  fi
  printf '{}\\r\\n'
else
  exit 64
fi
`)
  chmodSync(azPath, 0o755)

  writeFileSync(dataHelperPath, `
import { appendFileSync } from 'node:fs'
const operation = process.argv[2]
appendFileSync(process.env.MOCK_COMMAND_LOG, 'data ' + operation + '\\n')
if (operation === process.env.MOCK_FAIL_DATA_OPERATION) process.exit(66)
const values = {
  'map-group': { operation, mappingId: process.env.MOCK_MAPPING_ID, converged: true },
  'inspect-mapping': {
    operation,
    mappingId: process.env.MOCK_MAPPING_ID,
    tenantId: process.env.MOCK_TENANT_ID,
    groupObjectId: process.env.MOCK_GROUP_ID,
    role: 'recruiter',
    organizationId: process.env.MOCK_ORGANIZATION_ID,
    departmentId: process.env.MOCK_DEPARTMENT_ID,
    enabled: true,
    defaultDepartmentReady: true,
  },
  assign: { operation, assignmentId: process.env.MOCK_ASSIGNMENT_ID, changed: true, converged: true },
  'check-assignment': {
    operation,
    assignmentId: process.env.MOCK_ASSIGNMENT_ID,
    assignmentReady: true,
    organizationMembershipReady: true,
    defaultDepartmentReady: true,
    converged: true,
  },
  check: {
    operation,
    user: { objectId: process.env.MOCK_USER_ID },
    organizations: [],
    departments: [],
    assignments: [],
  },
  revoke: {
    operation,
    assignmentId: process.env.MOCK_ASSIGNMENT_ID,
    groupObjectId: process.env.MOCK_GROUP_ID,
    activeGroupDependencyCount: 0,
    sqlRevoked: true,
  },
  'check-revoke': { operation, assignmentId: process.env.MOCK_ASSIGNMENT_ID, sqlRevoked: true },
}
if (!values[operation]) process.exit(67)
process.stdout.write(JSON.stringify(values[operation]) + '\\n')
`)

  return {
    directory,
    commandLog,
    environmentFile,
    env: {
      ...process.env,
      PATH: `${directory}${delimiter}${process.env.PATH}`,
      ENTRA_ROLE_DATA_HELPER: dataHelperPath,
      MOCK_COMMAND_LOG: commandLog,
      MOCK_ACTIVE_TENANT: options.activeTenant ?? TENANT_ID,
      MOCK_ACTIVE_SUBSCRIPTION: options.activeSubscription ?? SUBSCRIPTION_ID,
      MOCK_GROUP_SECURITY_ENABLED: String(options.groupSecurityEnabled ?? true),
      MOCK_FAIL_GRAPH_WRITE: String(options.failGraphWrite ?? false),
      MOCK_FAIL_DATA_OPERATION: options.failDataOperation ?? '',
      MOCK_MEMBER_COUNT: String(options.memberCount ?? 1),
      MOCK_MEMBERSHIP_STATE: membershipState,
      MOCK_TENANT_ID: TENANT_ID,
      MOCK_API_SP_ID: API_SERVICE_PRINCIPAL_ID,
      MOCK_ROLE_ID: RECRUITER_ROLE_ID,
      MOCK_GROUP_ID: GROUP_ID,
      MOCK_ORGANIZATION_ID: ORGANIZATION_ID,
      MOCK_DEPARTMENT_ID: DEPARTMENT_ID,
      MOCK_USER_ID: USER_ID,
      MOCK_MAPPING_ID: MAPPING_ID,
      MOCK_ASSIGNMENT_ID: ASSIGNMENT_ID,
    },
  }
}

function runCli(args, options = {}) {
  const fixture = createCliFixture(options)
  const result = spawnSync(
    'bash',
    [resolve('infra/scripts/manage-entra-role.sh'), fixture.environmentFile, ...args],
    { cwd: process.cwd(), encoding: 'utf8', env: fixture.env },
  )

  const commands = readFileSync(fixture.commandLog, 'utf8')
  const summaries = result.stdout
    .split(/\r?\n/)
    .filter(line => line.startsWith('{'))
    .map(line => JSON.parse(line))

  return { ...result, commands, summary: summaries.at(-1) }
}

describe('Entra role-management SQL', () => {
  it('enforces exact role scope rules and explicit defaults', () => {
    assert.deepEqual(validateRoleScope({ role: 'admin' }), {
      role: 'admin', organizationId: null, departmentId: null,
    })
    assert.throws(
      () => validateRoleScope({ role: 'admin', organizationId: ORGANIZATION_ID }),
      /forbids organization/i,
    )
    assert.throws(
      () => validateRoleScope({ role: 'organization_admin' }),
      /requires organization/i,
    )
    assert.throws(
      () => validateRoleScope({ role: 'recruiter', organizationId: ORGANIZATION_ID }),
      /requires department/i,
    )
    assert.deepEqual(validateRoleScope({
      role: 'business_panel', organizationId: ORGANIZATION_ID,
    }), {
      role: 'business_panel', organizationId: ORGANIZATION_ID, departmentId: null,
    })
  })

  it('rejects incomplete action-specific SQL configuration', () => {
    assert.throws(
      () => validateRoleManagementConfiguration('assign', {
        tenantId: TENANT_ID,
        userObjectId: USER_ID,
        mappingId: MAPPING_ID,
        defaultDepartmentId: '',
      }),
      /defaultDepartmentId/,
    )
  })

  it('keeps check and mapping inspection read-only and parameterized', () => {
    for (const operation of ['check', 'inspect-mapping', 'check-revoke']) {
      const sql = buildRoleManagementSql(operation)
      assert.doesNotMatch(sql, /\b(?:INSERT|UPDATE|DELETE|MERGE)\b/i)
      assert.match(sql, /\[talentmatch\]\./)
      assertParameterized(sql, [TENANT_ID, USER_ID, MAPPING_ID, ASSIGNMENT_ID])
    }
  })

  it('maps a valid active scope idempotently in one audited transaction', () => {
    const sql = buildRoleManagementSql('map-group')

    assert.match(sql, /SET XACT_ABORT ON/i)
    assert.match(sql, /BEGIN TRANSACTION/i)
    assert.match(sql, /sp_getapplock/i)
    assert.match(sql, /\[talentmatch\]\.\[RoleGroupMappings\]/)
    assert.match(sql, /\[talentmatch\]\.\[Organizations\]/)
    assert.match(sql, /\[talentmatch\]\.\[Departments\]/)
    assert.match(sql, /auth\.role_group\.mapped/)
    assert.match(sql, /COMMIT TRANSACTION/i)
    assertParameterized(sql, [TENANT_ID, GROUP_ID, ORGANIZATION_ID, DEPARTMENT_ID])
  })

  it('assigns memberships before publishing an explicit default and group assignment', () => {
    const sql = buildRoleManagementSql('assign')
    const departmentMembership = sql.indexOf('INSERT INTO [talentmatch].[DepartmentMemberships]')
    const organizationMembership = sql.indexOf('INSERT INTO [talentmatch].[OrganizationMemberships]')
    const roleAssignment = sql.indexOf('INSERT INTO [talentmatch].[RoleAssignments]')

    assert.match(sql, /sp_getapplock/i)
    assert.match(sql, /Source[\s\S]*'group'/i)
    assert.match(sql, /RoleGroupMappingId/)
    assert.match(sql, /DefaultDepartmentMembershipId\s*=\s*@defaultDepartmentMembershipId/i)
    assert.match(sql, /AuthorizationVersion\s*=\s*AuthorizationVersion\s*\+\s*1/i)
    assert.match(sql, /auth\.assignment\.(?:activated|checked)/i)
    assert.ok(departmentMembership >= 0)
    assert.ok(organizationMembership > departmentMembership)
    assert.ok(roleAssignment > organizationMembership)
    assertParameterized(sql, [TENANT_ID, USER_ID, MAPPING_ID, DEPARTMENT_ID])
  })

  it('revokes only the requested assignment and retains dependency evidence', () => {
    const sql = buildRoleManagementSql('revoke')

    assert.match(sql, /WHERE Id = @assignmentId/i)
    assert.match(sql, /UserObjectId = @userObjectId/i)
    assert.match(sql, /Status = 'revoked'/i)
    assert.match(sql, /activeGroupDependencyCount/i)
    assert.match(sql, /auth\.assignment\.revoked/i)
    assert.doesNotMatch(sql, /DELETE\s+FROM\s+\[talentmatch\]\.\[RoleAssignments\]/i)
  })
})

describe('Entra role-management operator CLI', () => {
  it('maps a group after exact CRLF-safe context and Graph role validation', () => {
    const result = runCli([
      'map-group',
      '--group-object-id', GROUP_ID,
      '--role', 'recruiter',
      '--organization-id', ORGANIZATION_ID,
      '--department-id', DEPARTMENT_ID,
    ])

    assert.equal(result.status, 0, result.stderr)
    assert.equal(result.summary.command, 'map-group')
    assert.equal(result.summary.mappingId, MAPPING_ID)
    assert.equal(result.summary.converged, true)
    assert.doesNotMatch(result.stdout, /must-not-appear|accessToken|credential/i)
    assert.doesNotMatch(result.commands, /account set/i)
    assert.ok(result.commands.indexOf('appRoleAssignedTo') < result.commands.indexOf('data map-group'))
  })

  it('converges assignment in Graph-before-SQL order and reports only immutable IDs', () => {
    const result = runCli([
      'assign',
      '--user-object-id', USER_ID,
      '--mapping-id', MAPPING_ID,
      '--default-department-id', DEPARTMENT_ID,
    ], { memberCount: 0 })

    assert.equal(result.status, 0, result.stderr)
    assert.equal(result.summary.assignmentId, ASSIGNMENT_ID)
    assert.equal(result.summary.userObjectId, USER_ID)
    assert.equal(result.summary.converged, true)
    const inspectIndex = result.commands.indexOf('data inspect-mapping')
    const graphWriteIndex = result.commands.indexOf('--method post')
    const sqlAssignIndex = result.commands.indexOf('data assign')
    const sqlVerifyIndex = result.commands.indexOf('data check-assignment')
    assert.ok(inspectIndex >= 0 && inspectIndex < graphWriteIndex)
    assert.ok(graphWriteIndex < sqlAssignIndex)
    assert.ok(sqlAssignIndex < sqlVerifyIndex)
  })

  it('checks SQL, direct group membership, and enterprise app-role assignments without writes', () => {
    const result = runCli(['check', '--user-object-id', USER_ID])

    assert.equal(result.status, 0, result.stderr)
    assert.equal(result.summary.command, 'check')
    assert.equal(result.summary.userObjectId, USER_ID)
    assert.match(result.commands, /memberOf/)
    assert.match(result.commands, /appRoleAssignments/)
    assert.doesNotMatch(result.commands, /--method (?:post|delete|patch|put)/i)
  })

  it('revokes SQL before removing an otherwise-unused direct group membership', () => {
    const result = runCli([
      'revoke',
      '--user-object-id', USER_ID,
      '--assignment-id', ASSIGNMENT_ID,
    ])

    assert.equal(result.status, 0, result.stderr)
    assert.equal(result.summary.assignmentId, ASSIGNMENT_ID)
    assert.equal(result.summary.sqlRevoked, true)
    assert.equal(result.summary.groupMembershipRemoved, true)
    assert.ok(result.commands.indexOf('data revoke') < result.commands.indexOf('--method delete'))
    assert.ok(result.commands.indexOf('--method delete') < result.commands.indexOf('data check-revoke'))
    assert.ok(result.commands.lastIndexOf('/members?') > result.commands.indexOf('--method delete'))
  })

  it('uses the documented structured exit codes', () => {
    const cases = [
      {
        name: 'invalid arguments',
        args: ['map-group', '--role', 'recruiter'],
        options: {},
        exitCode: 2,
      },
      {
        name: 'context mismatch',
        args: ['check', '--user-object-id', USER_ID],
        options: { activeTenant: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee' },
        exitCode: 3,
      },
      {
        name: 'principal validation',
        args: [
          'map-group', '--group-object-id', GROUP_ID, '--role', 'recruiter',
          '--organization-id', ORGANIZATION_ID, '--department-id', DEPARTMENT_ID,
        ],
        options: { groupSecurityEnabled: false },
        exitCode: 4,
      },
      {
        name: 'Graph mutation',
        args: [
          'assign', '--user-object-id', USER_ID, '--mapping-id', MAPPING_ID,
          '--default-department-id', DEPARTMENT_ID,
        ],
        options: { memberCount: 0, failGraphWrite: true },
        exitCode: 5,
      },
      {
        name: 'SQL operation',
        args: [
          'map-group', '--group-object-id', GROUP_ID, '--role', 'recruiter',
          '--organization-id', ORGANIZATION_ID, '--department-id', DEPARTMENT_ID,
        ],
        options: { failDataOperation: 'map-group' },
        exitCode: 6,
      },
      {
        name: 'partial revocation',
        args: ['revoke', '--user-object-id', USER_ID, '--assignment-id', ASSIGNMENT_ID],
        options: { failGraphWrite: true },
        exitCode: 7,
      },
    ]

    for (const testCase of cases) {
      const result = runCli(testCase.args, testCase.options)
      assert.equal(result.status, testCase.exitCode, `${testCase.name}: ${result.stderr}`)
    }
  })
})