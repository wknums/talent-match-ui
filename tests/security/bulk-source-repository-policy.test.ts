import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { extname } from 'node:path'
import { describe, expect, it } from 'vitest'

const textExtensions = new Set([
  '.cs',
  '.csproj',
  '.json',
  '.md',
  '.mjs',
  '.ps1',
  '.razor',
  '.sh',
  '.sql',
  '.tf',
  '.ts',
  '.tsx',
  '.yaml',
  '.yml',
])

function repositoryFiles(): string[] {
  return execFileSync('git', ['ls-files', '--cached', '--others', '--exclude-standard'], {
    encoding: 'utf8',
  })
    .split(/\r?\n/)
    .filter(Boolean)
    .map((path) => path.replaceAll('\\', '/'))
}

function isMutablePolicySurface(path: string): boolean {
  if (path.startsWith('specs/001-sharepoint-bulk-scoring/validation/')) return false
  if (path.startsWith('specs/archive/')) return false
  if (path.startsWith('dotnet/src/Infrastructure/Persistence/Migrations/')) return false
  if (path.startsWith('tests/')) return false
  if (path === 'package-lock.json') return false

  return (
    path.startsWith('dotnet/src/') ||
    path.startsWith('server/') ||
    path.startsWith('src/') ||
    path.startsWith('infra/') ||
    path.startsWith('docs/') ||
    path.startsWith('.env') ||
    ['AUTHENTICATION.md', 'INTEGRATION.md', 'PARITY.md', 'README.md'].includes(path)
  )
}

const forbiddenRuntimePatterns: ReadonlyArray<[string, RegExp]> = [
  ['legacy same-tenant literal', /\bsame_tenant\b/i],
  ['legacy cross-tenant literal', /\bcross_tenant\b/i],
  ['legacy access-model field', /\b(?:AccessModel|accessModel)\b/],
  ['legacy owning-stack field', /\b(?:OwningStack|owningStack)\b/],
  ['Sites.Selected application permission', /\bSites\.Selected\b/i],
  ['source host/root allow-list', /\b(?:allowedHosts|SHAREPOINT_ALLOWED_HOSTS|SHAREPOINT_PERMITTED_ROOTS?|permittedRoots?)\b/i],
  ['source site/drive/folder allow-list', /\b(?:allowedSites?|allowedDrives?|allowedFolders?|sourceTenantAllowList)\b/i],
  ['app-only source reader', /\b(?:sharepoint_reader|SharePointReader|application-only|app-only)\b/i],
  ['source client secret', /\b(?:MICROSOFT365_SOURCE_CLIENT_SECRET|SHAREPOINT_READER_CLIENT_SECRET)\b/i],
  ['source certificate credential', /\b(?:SHAREPOINT_READER_CERTIFICATE|source_reader_certificate|client_certificate)\b/i],
  ['Graph application role declaration', /(?:type\s*=\s*"Role"|permission_type\s*=\s*"Application"|app_role_assignment)/i],
]

const concreteEnvironmentPatterns: ReadonlyArray<[string, RegExp]> = [
  ['nonblank source environment assignment', /^\s*(?:MICROSOFT365_SOURCE|SHAREPOINT|BULK_AUTH|BULK_ADMISSION)_[A-Z0-9_]+\s*=\s*\S+/im],
  ['concrete Azure resource ID', /\/subscriptions\/[0-9a-f-]+\/resourceGroups\/[^/\s"']+/i],
  ['concrete Azure host', /\b[a-z0-9-]+\.(?:vault\.azure\.net|blob\.core\.windows\.net|database\.windows\.net|azurewebsites\.net)\b/i],
]

describe('delegated Microsoft 365 source repository policy', () => {
  it('contains no old access mode, application role, source allow-list, or source credential path', () => {
    const violations: string[] = []
    for (const path of repositoryFiles().filter(isMutablePolicySurface)) {
      if (!textExtensions.has(extname(path)) && !path.startsWith('.env')) continue
      const content = readFileSync(path, 'utf8')
      for (const [name, pattern] of forbiddenRuntimePatterns) {
        if (pattern.test(content)) violations.push(`${path}: ${name}`)
      }
    }

    expect(violations.sort()).toEqual([])
  })

  it('keeps concrete environment identifiers out of mutable configuration and operational docs', () => {
    const violations: string[] = []
    for (const path of repositoryFiles().filter(isMutablePolicySurface)) {
      if (
        !path.startsWith('infra/') &&
        !path.startsWith('docs/') &&
        !path.startsWith('.env') &&
        !['AUTHENTICATION.md', 'INTEGRATION.md', 'README.md'].includes(path)
      ) {
        continue
      }
      if (!textExtensions.has(extname(path)) && !path.startsWith('.env')) continue
      const content = readFileSync(path, 'utf8')
        .split(/\r?\n/)
        .filter((line) => /MICROSOFT365_SOURCE|SHAREPOINT|BULK_AUTH|BULK_ADMISSION|source connector/i.test(line))
        .join('\n')
      for (const [name, pattern] of concreteEnvironmentPatterns) {
        if (pattern.test(content)) violations.push(`${path}: ${name}`)
      }
    }

    expect(violations.sort()).toEqual([])
  })
})
