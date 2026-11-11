import { chmodSync, mkdtempSync, readFileSync, readdirSync, rmSync, rmdirSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'
import { afterEach, describe, expect, it } from 'vitest'

import { synchronizeDeploymentProfile } from '../../infra/scripts/sync-deployment-profile.mjs'

const subscription = 'test-subscription'
const resourceId = (group: string, type: string, name: string) =>
  `/subscriptions/${subscription}/resourceGroups/${group}/providers/${type}/${name}`
const output = (value: unknown, sensitive = false) => ({ value, sensitive })
const configuration = {
  AZURE_SUBSCRIPTION_ID: subscription,
  STACK_TARGET: 'both',
  APP_AUTH_MODE: 'entra',
  AZ_KEY_VAULT_ENABLED: 'TRUE',
  AZ_APIM_ENABLED: 'TRUE',
  AZ_APP_INSIGHTS_ENABLED: 'TRUE',
  MICROSOFT365_SOURCE_MODE: 'graph',
}
const sharedOutputs = (): Record<string, ReturnType<typeof output>> => ({
  sql_server_id: output(resourceId('sql-rg', 'Microsoft.Sql/servers', 'deployed-sql')),
  sql_server_name: output('deployed-sql'),
  sql_database_name: output('deployed database'),
  sql_server_fqdn: output('deployed-sql.database.windows.net'),
  app_service_plan_id: output(resourceId('plan-rg', 'Microsoft.Web/serverfarms', 'deployed-plan')),
  key_vault_id: output(resourceId('vault-rg', 'Microsoft.KeyVault/vaults', 'deployed-vault')),
  key_vault_name: output('deployed-vault'),
  key_vault_uri: output('https://deployed-vault.vault.azure.net/'),
  apim_id: output(resourceId('api-rg', 'Microsoft.ApiManagement/service', 'deployed-apim')),
  apim_gateway_url: output('https://deployed-apim.azure-api.net'),
  app_insights_id: output(resourceId('monitoring-rg', 'Microsoft.Insights/components', 'deployed-insights')),
  app_insights_connection_string: output('sensitive-output-canary', true),
  identity_stack_a_id: output(resourceId('identity-rg', 'Microsoft.ManagedIdentity/userAssignedIdentities', 'identity-a')),
  identity_stack_b_id: output(resourceId('identity-rg', 'Microsoft.ManagedIdentity/userAssignedIdentities', 'identity-b')),
  entra_api_app_client_id: output('api-client'),
  entra_api_service_principal_object_id: output('api-principal'),
  entra_api_identifier_uri: output('api://approved-api'),
  entra_api_scope: output('access_as_user'),
  entra_api_scope_id: output('scope-id'),
  entra_stack_a_client_id: output('spa-a'),
  entra_stack_b_client_id: output('spa-b'),
  entra_stack_a_redirect_uris: output(['https://approved-a.example/']),
  entra_stack_b_redirect_uris: output(['https://approved-b.example/authentication/login-callback']),
  entra_app_role_ids: output({
    admin: 'admin-role',
    organization_admin: 'organization-admin-role',
    recruiter: 'recruiter-role',
    business_panel: 'panel-role',
  }),
  source_connector_client_id: output('source-client'),
  source_connector_redirect_uris: output([]),
  bulk_auth_key_uri: output('https://deployed-vault.vault.azure.net/keys/bulk-auth'),
})
const apps = (): Record<string, Record<string, ReturnType<typeof output>>> => ({
  'stack-a': { app_url: output('https://actual-a-unique.azurewebsites.net'), app_name: output('actual-a') },
  'stack-b': { app_url: output('https://actual-b-unique.azurewebsites.net'), app_name: output('actual-b') },
})
const files: string[] = []

function profile(content = '') {
  const directory = mkdtempSync(join(tmpdir(), 'talentmatch-profile-'))
  const path = join(directory, 'customer profile.env.azure')
  writeFileSync(path, content)
  files.push(path)
  return path
}

afterEach(() => {
  for (const path of files.splice(0)) {
    rmSync(path)
    rmdirSync(join(path, '..'))
  }
})

describe('generated shared deployment settings', () => {
  it('saves SQL coordinates and fills public resource, Entra, identity, and source settings', () => {
    const path = profile()
    synchronizeDeploymentProfile(path, 'shared', { shared: sharedOutputs() }, configuration)
    const saved = readFileSync(path, 'utf8')
    for (const assignment of [
      'SQL_RG="sql-rg"', 'SQL_DATABASE_NAME="deployed database"',
      'AZURE_SQL_SERVER_FQDN="deployed-sql.database.windows.net"',
      'AZURE_SQL_DATABASE_NAME="deployed database"',
      'AZ_APP_SERVICE_PLAN_NAME="deployed-plan"', 'AZ_APP_SERVICE_PLAN_RG="plan-rg"',
      'AZ_KEY_VAULT_NAME="deployed-vault"', 'AZ_KEY_VAULT_RG="vault-rg"',
      'KEY_VAULT_URI="https://deployed-vault.vault.azure.net/"',
      'AZ_APIM_NAME="deployed-apim"', 'AZ_APIM_RG="api-rg"',
      'APIM_GATEWAY_URL="https://deployed-apim.azure-api.net"',
      'AZ_APP_INSIGHTS_NAME="deployed-insights"', 'AZ_APP_INSIGHTS_RG="monitoring-rg"',
      'AZ_IDENTITY_STACK_A_NAME="identity-a"', 'AZ_IDENTITY_STACK_B_NAME="identity-b"',
      'AZ_IDENTITIES_RG="identity-rg"',
      'ENTRA_API_APP_CLIENT_ID="api-client"', 'ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID="api-principal"',
      'ENTRA_API_IDENTIFIER_URI="api://approved-api"', 'ENTRA_API_SCOPE_ID="scope-id"',
      'ENTRA_ADMIN_APP_ROLE_ID="admin-role"', 'ENTRA_RECRUITER_APP_ROLE_ID="recruiter-role"',
      'ENTRA_STACK_A_CLIENT_ID="spa-a"', 'ENTRA_STACK_B_CLIENT_ID="spa-b"',
      'ENTRA_STACK_A_REDIRECT_URIS="https://approved-a.example/"',
      'MICROSOFT365_SOURCE_CLIENT_ID="source-client"',
      'BULK_AUTH_KEY_URI="https://deployed-vault.vault.azure.net/keys/bulk-auth"',
    ]) expect(saved).toContain(`${assignment}\n`)
    expect(saved).not.toContain('sensitive-output-canary')
    expect(saved).not.toMatch(/REUSE=|PASSWORD=|PUBLISHER_VERIFIED=|CORS_ALLOWED_ORIGINS=/)
  })

  it('keeps operator values and custom URLs, but refreshes authoritative SQL coordinates', () => {
    const original = [
      '# Customer configuration',
      'SQL_RG=stale',
      'ENTRA_API_APP_CLIENT_ID=approved-client',
      'ENTRA_STACK_B_REDIRECT_URIS=https://custom.example/sign-in',
      'MICROSOFT365_SOURCE_REDIRECT_URIS=https://custom.example/callback',
      'BULK_AUTH_KEY_URI=https://custom-vault.vault.azure.net/keys/approved',
      'AZ_SQL_REUSE=FALSE',
      'PASSWORD="private-canary"',
      '',
    ].join('\n')
    const path = profile(original)
    synchronizeDeploymentProfile(path, 'shared', { shared: sharedOutputs() }, configuration)
    const saved = readFileSync(path, 'utf8')
    for (const line of original.split('\n').filter(line => line && !line.startsWith('SQL_RG='))) {
      expect(saved).toContain(`${line}\n`)
    }
    expect(saved).toContain('SQL_RG="sql-rg"\n')
  })

  it('uses registered source callbacks when available, without generating replacements', () => {
    const path = profile('MICROSOFT365_SOURCE_REDIRECT_URIS=""\n')
    const outputs = sharedOutputs()
    outputs.source_connector_redirect_uris = output(['https://approved.example/callback', 'https://approved.example/admin'])
    synchronizeDeploymentProfile(path, 'shared', { shared: outputs }, configuration)
    const result = synchronizeDeploymentProfile(path, 'apps', apps(), configuration)
    expect(readFileSync(path, 'utf8')).toContain(
      'MICROSOFT365_SOURCE_REDIRECT_URIS="https://approved.example/callback,https://approved.example/admin"',
    )
    expect(result.warnings).toEqual([])
  })

  it('does not fill settings for disabled features or unselected stacks', () => {
    const path = profile()
    const outputs = sharedOutputs()
    delete outputs.identity_stack_a_id
    synchronizeDeploymentProfile(path, 'shared', { shared: outputs }, {
      ...configuration, STACK_TARGET: 'stack-b', APP_AUTH_MODE: 'simple',
      AZ_KEY_VAULT_ENABLED: 'FALSE', AZ_APIM_ENABLED: 'FALSE', AZ_APP_INSIGHTS_ENABLED: 'FALSE',
      MICROSOFT365_SOURCE_MODE: 'mock',
    })
    const saved = readFileSync(path, 'utf8')
    expect(saved).toContain('AZ_IDENTITY_STACK_B_NAME="identity-b"')
    expect(saved).not.toMatch(/STACK_A|ENTRA_|KEY_VAULT|APIM|INSIGHTS|MICROSOFT365|BULK_AUTH/)
  })

  it.each(['sql_server_id', 'sql_server_name', 'sql_database_name', 'sql_server_fqdn'])(
    'rejects missing %s before modifying the profile', key => {
      const path = profile('# unchanged\n')
      const outputs = sharedOutputs()
      delete outputs[key]
      expect(() => synchronizeDeploymentProfile(path, 'shared', { shared: outputs }, configuration)).toThrow(key)
      expect(readFileSync(path, 'utf8')).toBe('# unchanged\n')
    },
  )

  it.each([
    'invalid',
    resourceId('sql-rg', 'Microsoft.Sql/servers', 'different-server'),
    resourceId('sql-rg', 'Microsoft.Sql/servers', 'deployed-sql').replace(subscription, 'different-subscription'),
  ])('rejects inconsistent SQL resource IDs without a partial update', id => {
    const path = profile('# unchanged\n')
    const outputs = sharedOutputs()
    outputs.sql_server_id = output(id)
    expect(() => synchronizeDeploymentProfile(path, 'shared', { shared: outputs }, configuration))
      .toThrow('SQL server resource ID')
    expect(readFileSync(path, 'utf8')).toBe('# unchanged\n')
  })

  it('rejects a sensitive output rather than copying it into a public setting', () => {
    const path = profile('# unchanged\n')
    const outputs = sharedOutputs()
    outputs.source_connector_client_id = output('sensitive-canary', true)
    expect(() => synchronizeDeploymentProfile(path, 'shared', { shared: outputs }, configuration))
      .toThrow('source_connector_client_id')
    expect(readFileSync(path, 'utf8')).toBe('# unchanged\n')
  })

  it('rejects identities from different resource groups instead of guessing AZ_IDENTITIES_RG', () => {
    const path = profile()
    const outputs = sharedOutputs()
    outputs.identity_stack_b_id = output(resourceId('other-rg', 'Microsoft.ManagedIdentity/userAssignedIdentities', 'identity-b'))
    expect(() => synchronizeDeploymentProfile(path, 'shared', { shared: outputs }, configuration))
      .toThrow('AZ_IDENTITIES_RG')
  })

  it.each(['key_vault_id', 'apim_id', 'app_insights_id'])(
    'rejects an empty %s for an enabled feature', key => {
      const path = profile('# unchanged\n')
      const outputs = sharedOutputs()
      outputs[key] = output('')
      expect(() => synchronizeDeploymentProfile(path, 'shared', { shared: outputs }, configuration)).toThrow(key)
      expect(readFileSync(path, 'utf8')).toBe('# unchanged\n')
    },
  )

  it('warns when older state does not yet expose optional resource/redirect outputs', () => {
    const path = profile()
    const outputs = sharedOutputs()
    delete outputs.key_vault_id
    delete outputs.entra_stack_b_redirect_uris
    const result = synchronizeDeploymentProfile(path, 'shared', { shared: outputs }, configuration)
    expect(result.warnings).toHaveLength(2)
    expect(result.warnings.join('\n')).toContain('key_vault_id')
    expect(result.warnings.join('\n')).toContain('entra_stack_b_redirect_uris')
    expect(readFileSync(path, 'utf8')).not.toContain('AZ_KEY_VAULT_RG=')
  })
})

describe('post-deployment callback and app settings', () => {
  it('fills both actual host URLs and both source callback paths for each deployed stack', () => {
    const path = profile('MICROSOFT365_SOURCE_REDIRECT_URIS=\n')
    const result = synchronizeDeploymentProfile(path, 'apps', apps(), configuration)
    const saved = readFileSync(path, 'utf8')
    expect(saved).toContain('E2E_STACK_A_BASE_URL="https://actual-a-unique.azurewebsites.net"')
    expect(saved).toContain('E2E_STACK_B_BASE_URL="https://actual-b-unique.azurewebsites.net"')
    for (const host of ['actual-a-unique', 'actual-b-unique']) {
      expect(saved).toContain(`https://${host}.azurewebsites.net/api/source-connections/callback`)
      expect(saved).toContain(`https://${host}.azurewebsites.net/api/source-connections/admin-consent-callback`)
    }
    expect(result.warnings).toHaveLength(1)
    expect(result.warnings[0]).toMatch(/register|registration/i)
  })

  it('preserves explicitly configured callback lists and browser targets', () => {
    const original = 'MICROSOFT365_SOURCE_REDIRECT_URIS=https://custom.example/callback\nE2E_STACK_B_BASE_URL=https://custom.example\n'
    const path = profile(original)
    const result = synchronizeDeploymentProfile(path, 'apps', { 'stack-b': apps()['stack-b'] }, configuration)
    expect(readFileSync(path, 'utf8')).toBe(original)
    expect(result.warnings).toEqual([])
  })

  it('uses configured callback paths and never guesses an unselected stack hostname', () => {
    const path = profile()
    synchronizeDeploymentProfile(path, 'apps', { 'stack-b': apps()['stack-b'] }, {
      ...configuration, STACK_TARGET: 'stack-b',
      MICROSOFT365_SOURCE_CALLBACK_PATH: '/custom/oauth',
      MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH: '/custom/admin',
    })
    const saved = readFileSync(path, 'utf8')
    expect(saved).toContain('https://actual-b-unique.azurewebsites.net/custom/oauth')
    expect(saved).toContain('https://actual-b-unique.azurewebsites.net/custom/admin')
    expect(saved).not.toContain('STACK_A')
  })

  it('does not invent connector policy, endpoints, or permissions in mock mode', () => {
    const path = profile()
    synchronizeDeploymentProfile(path, 'apps', apps(), { ...configuration, MICROSOFT365_SOURCE_MODE: 'mock' })
    expect(readFileSync(path, 'utf8')).not.toMatch(/MICROSOFT365|BULK_|CORS|AUTHORITY|SCOPES/)
  })

  it.each(['//untrusted.example/callback', 'https://untrusted.example/callback', '/\\untrusted', '/callback?query=1'])(
    'rejects unsafe callback path %s', callback => {
      const path = profile('# unchanged\n')
      expect(() => synchronizeDeploymentProfile(path, 'apps', apps(), {
        ...configuration, MICROSOFT365_SOURCE_CALLBACK_PATH: callback,
      })).toThrow('MICROSOFT365_SOURCE_CALLBACK_PATH')
      expect(readFileSync(path, 'utf8')).toBe('# unchanged\n')
    },
  )

  it.each(['http://insecure.example', 'https://example.com/unexpected/path', 'not-a-url'])(
    'rejects invalid deployed app URL %s', appUrl => {
      const path = profile()
      const outputs = apps()
      outputs['stack-b'].app_url = output(appUrl)
      expect(() => synchronizeDeploymentProfile(path, 'apps', outputs, configuration)).toThrow('app_url')
      expect(readFileSync(path, 'utf8')).toBe('')
    },
  )
})

describe('environment-file preservation', () => {
  it('handles quoted blanks and duplicate entries, preserving CRLF, permissions, and idempotency', () => {
    const path = profile('# heading\r\n SQL_RG = old\r\nSQL_RG=\r\nENTRA_STACK_B_CLIENT_ID=""\r\nLAST=keep')
    chmodSync(path, 0o600)
    const mode = statSync(path).mode
    synchronizeDeploymentProfile(path, 'shared', { shared: sharedOutputs() }, configuration)
    const first = readFileSync(path, 'utf8')
    expect(first.match(/SQL_RG="sql-rg"/g)).toHaveLength(2)
    expect(first).toContain('ENTRA_STACK_B_CLIENT_ID="spa-b"\r\n')
    expect(first).toContain('LAST=keep\r\n')
    expect(first.replace(/\r\n/g, '')).not.toContain('\n')
    expect(statSync(path).mode).toBe(mode)
    const result = synchronizeDeploymentProfile(path, 'shared', { shared: sharedOutputs() }, configuration)
    expect(result.writtenKeys).toEqual([])
    expect(readFileSync(path, 'utf8')).toBe(first)
    expect(readdirSync(join(path, '..'))).toEqual(['customer profile.env.azure'])
  })

  it('reports malformed CLI input without exposing Terraform output or profile values', () => {
    const path = profile('PRIVATE_SETTING="private-canary"\n')
    const result = spawnSync(process.execPath, [
      join(process.cwd(), 'infra', 'scripts', 'sync-deployment-profile.mjs'), path, 'shared',
    ], { input: '{"secret":"sensitive-canary", invalid', encoding: 'utf8' })
    expect(result.status).toBe(1)
    expect(result.stderr).toContain('Terraform outputs are not valid JSON')
    expect(result.stdout + result.stderr).not.toMatch(/sensitive-canary|private-canary/)
    expect(readFileSync(path, 'utf8')).toBe('PRIVATE_SETTING="private-canary"\n')
  })
})
