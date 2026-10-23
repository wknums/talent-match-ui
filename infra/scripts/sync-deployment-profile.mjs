import fs from 'node:fs'
import { randomUUID } from 'node:crypto'
import { pathToFileURL } from 'node:url'

const sqlKeys = new Set(['SQL_RG', 'SQL_SERVER_NAME', 'SQL_DATABASE_NAME', 'SQL_SERVER_FQDN'])

function collectUpdates(phase, snapshot, configuration) {
  const values = new Map()
  const warnings = []
  const selected = stack => configuration.STACK_TARGET === `stack-${stack}`
    || (configuration.STACK_TARGET ?? 'both') === 'both'
  const graph = configuration.MICROSOFT365_SOURCE_MODE === 'graph'
  let generatedCallbacks = false

  const string = (outputs, name, required = false) => {
    const entry = outputs[name]
    if (entry?.sensitive) throw new Error(`Refusing to persist sensitive Terraform output '${name}'.`)
    const value = entry?.value
    if (value == null || value === '') {
      if (required) throw new Error(`Terraform output '${name}' is unavailable; the profile was not updated.`)
      return undefined
    }
    if (typeof value !== 'string' || !value.trim() || /[\r\n]/.test(value)) {
      throw new Error(`Terraform output '${name}' is invalid; the profile was not updated.`)
    }
    return value
  }
  const copy = (outputs, name, key, required = false) => {
    const value = string(outputs, name, required)
    if (value !== undefined) values.set(key, value)
  }
  const copyUris = (outputs, name, key) => {
    const entry = outputs[name]
    if (entry === undefined) {
      warnings.push(`Shared output '${name}' is not in this state. Apply the current shared configuration to expose its redirect list.`)
      return
    }
    if (entry.sensitive || !Array.isArray(entry.value)
      || entry.value.some(value => typeof value !== 'string' || !value.trim() || /[\r\n,]/.test(value))) {
      throw new Error(`Terraform output '${name}' must contain public redirect URIs.`)
    }
    if (entry.value.length > 0) values.set(key, [...new Set(entry.value)].join(','))
  }
  const coordinates = (outputs, name, type, nameKey, groupKey, required = true) => {
    const id = string(outputs, name, required)
    if (id === undefined) return
    const match = /^\/subscriptions\/([^/]+)\/resourceGroups\/([^/]+)\/providers\/([^/]+\/[^/]+)\/([^/]+)$/i.exec(id)
    const label = name === 'sql_server_id' ? 'SQL server resource ID' : `resource ID in '${name}'`
    if (!match || match[1].toLowerCase() !== configuration.AZURE_SUBSCRIPTION_ID?.toLowerCase()
      || match[3].toLowerCase() !== type.toLowerCase()) {
      throw new Error(`Invalid ${label}; the profile was not updated.`)
    }
    if (groupKey === 'AZ_IDENTITIES_RG' && values.has(groupKey)
      && values.get(groupKey).toLowerCase() !== match[2].toLowerCase()) {
      throw new Error('Selected identities do not share one AZ_IDENTITIES_RG; the profile was not updated.')
    }
    values.set(nameKey, match[4])
    values.set(groupKey, match[2])
  }

  if (phase === 'shared') {
    const outputs = snapshot.shared
    if (!outputs) throw new Error('Shared Terraform outputs are unavailable.')
    coordinates(outputs, 'sql_server_id', 'Microsoft.Sql/servers', 'SQL_SERVER_NAME', 'SQL_RG')
    const serverName = string(outputs, 'sql_server_name', true)
    if (values.get('SQL_SERVER_NAME').toLowerCase() !== serverName.toLowerCase()) {
      throw new Error('SQL server resource ID does not match sql_server_name; the profile was not updated.')
    }
    values.set('SQL_SERVER_NAME', serverName)
    copy(outputs, 'sql_database_name', 'SQL_DATABASE_NAME', true)
    copy(outputs, 'sql_server_fqdn', 'SQL_SERVER_FQDN', true)
    copy(outputs, 'sql_database_name', 'AZURE_SQL_DATABASE_NAME')
    copy(outputs, 'sql_server_fqdn', 'AZURE_SQL_SERVER_FQDN')
    coordinates(outputs, 'app_service_plan_id', 'Microsoft.Web/serverfarms', 'AZ_APP_SERVICE_PLAN_NAME', 'AZ_APP_SERVICE_PLAN_RG')

    for (const stack of ['a', 'b']) {
      if (selected(stack)) {
        coordinates(outputs, `identity_stack_${stack}_id`, 'Microsoft.ManagedIdentity/userAssignedIdentities',
          `AZ_IDENTITY_STACK_${stack.toUpperCase()}_NAME`, 'AZ_IDENTITIES_RG')
      }
    }
    for (const [flag, name, type, nameKey, groupKey] of [
      ['AZ_KEY_VAULT_ENABLED', 'key_vault_id', 'Microsoft.KeyVault/vaults', 'AZ_KEY_VAULT_NAME', 'AZ_KEY_VAULT_RG'],
      ['AZ_APIM_ENABLED', 'apim_id', 'Microsoft.ApiManagement/service', 'AZ_APIM_NAME', 'AZ_APIM_RG'],
      ['AZ_APP_INSIGHTS_ENABLED', 'app_insights_id', 'Microsoft.Insights/components', 'AZ_APP_INSIGHTS_NAME', 'AZ_APP_INSIGHTS_RG'],
    ]) {
      if (configuration[flag]?.toUpperCase() !== 'TRUE') continue
      if (outputs[name] === undefined) {
        warnings.push(`Shared output '${name}' is not in this state. Apply the current shared configuration to expose its resource coordinates.`)
      } else {
        coordinates(outputs, name, type, nameKey, groupKey)
      }
    }
    if (configuration.AZ_KEY_VAULT_ENABLED?.toUpperCase() === 'TRUE') {
      copy(outputs, 'key_vault_name', 'AZ_KEY_VAULT_NAME')
      copy(outputs, 'key_vault_uri', 'KEY_VAULT_URI', true)
    }
    if (configuration.AZ_APIM_ENABLED?.toUpperCase() === 'TRUE') {
      copy(outputs, 'apim_gateway_url', 'APIM_GATEWAY_URL', true)
    }
    if ((configuration.APP_AUTH_MODE ?? 'entra') === 'entra') {
      for (const [name, key] of [
        ['entra_api_app_client_id', 'ENTRA_API_APP_CLIENT_ID'],
        ['entra_api_service_principal_object_id', 'ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID'],
        ['entra_api_identifier_uri', 'ENTRA_API_IDENTIFIER_URI'],
        ['entra_api_scope', 'ENTRA_API_SCOPE'],
        ['entra_api_scope_id', 'ENTRA_API_SCOPE_ID'],
      ]) copy(outputs, name, key, true)
      const roles = outputs.entra_app_role_ids
      if (roles?.sensitive || !roles?.value || typeof roles.value !== 'object' || Array.isArray(roles.value)) {
        throw new Error("Terraform output 'entra_app_role_ids' is unavailable or invalid.")
      }
      for (const role of ['admin', 'organization_admin', 'recruiter', 'business_panel']) {
        const id = roles.value[role]
        if (typeof id !== 'string' || !id.trim() || /[\r\n]/.test(id)) {
          throw new Error(`Terraform output 'entra_app_role_ids' is missing a valid ${role} ID.`)
        }
        values.set(`ENTRA_${role.toUpperCase()}_APP_ROLE_ID`, id)
      }
      for (const stack of ['a', 'b']) {
        if (!selected(stack)) continue
        copy(outputs, `entra_stack_${stack}_client_id`, `ENTRA_STACK_${stack.toUpperCase()}_CLIENT_ID`, true)
        copyUris(outputs, `entra_stack_${stack}_redirect_uris`, `ENTRA_STACK_${stack.toUpperCase()}_REDIRECT_URIS`)
      }
    }
    if (graph) {
      copy(outputs, 'source_connector_client_id', 'MICROSOFT365_SOURCE_CLIENT_ID', true)
      copy(outputs, 'bulk_auth_key_uri', 'BULK_AUTH_KEY_URI', true)
      copyUris(outputs, 'source_connector_redirect_uris', 'MICROSOFT365_SOURCE_REDIRECT_URIS')
    }
  } else if (phase === 'apps') {
    const callbacks = []
    for (const stack of ['a', 'b']) {
      if (!selected(stack) || !snapshot[`stack-${stack}`]) continue
      const outputs = snapshot[`stack-${stack}`]
      const appUrl = string(outputs, 'app_url', true)
      let url
      try {
        url = new URL(appUrl)
      } catch {
        throw new Error(`Terraform output 'app_url' for Stack ${stack.toUpperCase()} is invalid.`)
      }
      if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/'
        || url.search || url.hash) {
        throw new Error(`Terraform output 'app_url' for Stack ${stack.toUpperCase()} must be an HTTPS origin.`)
      }
      values.set(`E2E_STACK_${stack.toUpperCase()}_BASE_URL`, url.origin)
      if (!graph) continue
      for (const [key, fallback] of [
        ['MICROSOFT365_SOURCE_CALLBACK_PATH', '/api/source-connections/callback'],
        ['MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH', '/api/source-connections/admin-consent-callback'],
      ]) {
        const path = configuration[key]?.trim() || fallback
        if (!path.startsWith('/') || path.startsWith('//') || /[\\?#\r\n]/.test(path)) {
          throw new Error(`${key} must be a local absolute callback path.`)
        }
        callbacks.push(`${url.origin}${path}`)
      }
    }
    if (callbacks.length > 0) {
      values.set('MICROSOFT365_SOURCE_REDIRECT_URIS', [...new Set(callbacks)].join(','))
      generatedCallbacks = true
    }
  } else {
    throw new Error("Profile sync phase must be 'shared' or 'apps'.")
  }
  return { values, warnings, generatedCallbacks }
}

export function synchronizeDeploymentProfile(profile, phase, snapshot, configuration = process.env) {
  const { values, warnings, generatedCallbacks } = collectUpdates(phase, snapshot, configuration)
  const file = fs.realpathSync(profile)
  const original = fs.readFileSync(file, 'utf8')
  const newline = original.includes('\r\n') ? '\r\n' : '\n'
  const pattern = /^([ \t]*)([A-Za-z_][A-Za-z0-9_]*)[ \t]*=([^\r\n]*)/gm
  const existing = new Map([...original.matchAll(pattern)].map(match => [match[2], match[3].trim()]))
  const blank = value => value === undefined || value === '' || /^(['"])\s*\1$/.test(value)
  const updates = new Map([...values].filter(([key]) => sqlKeys.has(key) || blank(existing.get(key))))
  const seen = new Set()
  const written = new Set()
  let updated = original.replace(pattern, (line, indent, key) => {
    if (!updates.has(key)) return line
    seen.add(key)
    const replacement = `${indent}${key}=${JSON.stringify(updates.get(key))}`
    if (replacement !== line) written.add(key)
    return replacement
  })
  for (const [key, value] of updates) {
    if (seen.has(key)) continue
    if (updated.length > 0 && !updated.endsWith('\n')) updated += newline
    updated += `${key}=${JSON.stringify(value)}${newline}`
    written.add(key)
  }
  if (updated !== original) {
    const temporary = `${file}.${randomUUID()}.tmp`
    try {
      fs.writeFileSync(temporary, updated, { flag: 'wx', mode: 0o600 })
      fs.chmodSync(temporary, fs.statSync(file).mode & 0o777)
      fs.renameSync(temporary, file)
    } finally {
      fs.rmSync(temporary, { force: true })
    }
  }
  if (generatedCallbacks && written.has('MICROSOFT365_SOURCE_REDIRECT_URIS')) {
    warnings.push('Generated MICROSOFT365_SOURCE_REDIRECT_URIS from deployed hosts. Review and register these exact Web callbacks in Entra before source sign-in; no registration or consent was changed.')
  }
  return { writtenKeys: [...written], warnings }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [profile, phase] = process.argv.slice(2)
    if (!profile || !phase || process.argv.length !== 4) {
      throw new Error('Usage: sync-deployment-profile.mjs <env-file> shared|apps (Terraform output JSON on stdin)')
    }
    let snapshot
    try {
      snapshot = JSON.parse(fs.readFileSync(0, 'utf8'))
    } catch (error) {
      if (!(error instanceof SyntaxError)) throw error
      throw new Error('Terraform outputs are not valid JSON; the profile was not updated.')
    }
    const result = synchronizeDeploymentProfile(profile, phase, snapshot)
    console.log(result.writtenKeys.length > 0
      ? `[OK] Saved deployment settings to ${profile}: ${result.writtenKeys.join(', ')}`
      : `[OK] Deployment settings are already populated in ${profile}`)
    for (const warning of result.warnings) console.warn(`[WARN] ${warning}`)
  } catch (error) {
    console.error(`[ERROR] ${error.message}`)
    process.exitCode = 1
  }
}
