import { afterEach, describe, expect, it, vi } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

describe('Stack A MSAL configuration', () => {
  afterEach(() => {
    vi.unstubAllEnvs()
    vi.resetModules()
  })

  it('requests the configured API identifier URI instead of assuming api://<client-id>', async () => {
    vi.stubEnv('VITE_ENTRA_API_IDENTIFIER_URI', 'api://tenant.example/talent-api')
    vi.stubEnv('VITE_ENTRA_API_SCOPE', 'access_as_user')

    const { getEntraApiScope } = await import('../../src/lib/msal-config')

    expect(getEntraApiScope()).toBe(
      'api://tenant.example/talent-api/access_as_user',
    )
  })

  it('normalizes a trailing slash in the API identifier URI', async () => {
    vi.stubEnv('VITE_ENTRA_API_IDENTIFIER_URI', 'api://tenant.example/talent-api/')
    vi.stubEnv('VITE_ENTRA_API_SCOPE', 'access_as_user')

    const { getEntraApiScope } = await import('../../src/lib/msal-config')

    expect(getEntraApiScope()).toBe(
      'api://tenant.example/talent-api/access_as_user',
    )
  })

  it('lets MsalProvider own initialization and redirect handling', () => {
    const mainSource = readFileSync(resolve('src/main.tsx'), 'utf8')

    expect(mainSource).toContain(
      '<MsalProvider instance={createMsalInstance()}>{app}</MsalProvider>',
    )
    expect(mainSource).not.toContain('initializeMsal')
    expect(mainSource).not.toContain('handleRedirectPromise')
  })
})
