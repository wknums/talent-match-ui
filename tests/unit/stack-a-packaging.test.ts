import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { describe, expect, it } from 'vitest'

const packageScript = readFileSync(
  resolve('infra/scripts/package-stack-a.sh'),
  'utf8',
)

describe('Stack A packaging', () => {
  it('maps runtime Entra settings into Vite build settings', () => {
    expect(packageScript).toMatch(
      /VITE_APP_AUTH_MODE="\$\{VITE_APP_AUTH_MODE:-\$\{APP_AUTH_MODE:-simple\}\}"/,
    )
    expect(packageScript).toMatch(
      /VITE_ENTRA_TENANT_ID="\$\{VITE_ENTRA_TENANT_ID:-\$\{AZURE_TENANT_ID:-\}\}"/,
    )
    expect(packageScript).toMatch(
      /VITE_ENTRA_STACK_A_CLIENT_ID="\$\{VITE_ENTRA_STACK_A_CLIENT_ID:-\$\{ENTRA_STACK_A_CLIENT_ID:-\}\}"/,
    )
    expect(packageScript).toMatch(
      /VITE_ENTRA_API_APP_CLIENT_ID="\$\{VITE_ENTRA_API_APP_CLIENT_ID:-\$\{ENTRA_API_APP_CLIENT_ID:-\}\}"/,
    )
  })

  it('fails an Entra build when required public configuration is missing', () => {
    for (const name of [
      'VITE_ENTRA_TENANT_ID',
      'VITE_ENTRA_STACK_A_CLIENT_ID',
      'VITE_ENTRA_API_APP_CLIENT_ID',
      'VITE_ENTRA_API_SCOPE',
    ]) {
      expect(packageScript).toContain(`\t\t"${name}"`)
    }
  })

  it('stamps the compiled authentication mode into build metadata', () => {
    expect(packageScript).toMatch(/"authMode": "\$VITE_APP_AUTH_MODE"/)
  })

  it('reuses validated local dependencies but supports clean CI installs', () => {
    expect(packageScript).toMatch(/INSTALL_MODE="\$\{STACK_A_INSTALL_MODE:-auto\}"/)
    expect(packageScript).toContain('node_modules/.package-lock.json -nt package-lock.json')
    expect(packageScript).toContain('node_modules/.package-lock.json -nt package.json')
    expect(packageScript).toMatch(/clean\)[\s\S]*?npm ci/)
  })

  it('builds the client with Vite without a no-output TypeScript scan', () => {
    expect(packageScript).toContain('npm run build:client')
    expect(packageScript).not.toContain('npm run build\n')
  })
})