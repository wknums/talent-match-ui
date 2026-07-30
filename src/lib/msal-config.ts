import {
  BrowserCacheLocation,
  PublicClientApplication,
  type Configuration,
  type RedirectRequest,
} from '@azure/msal-browser'

import type { AuthenticationProvider } from '@/types'

export const appAuthMode: AuthenticationProvider = import.meta.env.VITE_APP_AUTH_MODE === 'entra'
  ? 'entra'
  : 'simple'

const tenantId = import.meta.env.VITE_ENTRA_TENANT_ID?.trim()
const stackAClientId = import.meta.env.VITE_ENTRA_STACK_A_CLIENT_ID?.trim()
const apiClientId = import.meta.env.VITE_ENTRA_API_APP_CLIENT_ID?.trim()
const apiScopeName = import.meta.env.VITE_ENTRA_API_SCOPE?.trim() || 'access_as_user'

function requireEntraValue(value: string | undefined, name: string): string {
  if (!value) {
    throw new Error(`Missing required public Entra configuration: ${name}`)
  }
  return value
}

export function getEntraApiScope(): string {
  return `api://${requireEntraValue(apiClientId, 'VITE_ENTRA_API_APP_CLIENT_ID')}/${apiScopeName}`
}

export function getEntraLoginRequest(): RedirectRequest {
  return { scopes: [getEntraApiScope()] }
}

export function createMsalInstance(): PublicClientApplication {
  const config: Configuration = {
    auth: {
      clientId: requireEntraValue(stackAClientId, 'VITE_ENTRA_STACK_A_CLIENT_ID'),
      authority: `https://login.microsoftonline.com/${requireEntraValue(tenantId, 'VITE_ENTRA_TENANT_ID')}`,
      redirectUri: window.location.origin,
      postLogoutRedirectUri: window.location.origin,
      navigateToLoginRequestUrl: false,
    },
    cache: {
      cacheLocation: BrowserCacheLocation.SessionStorage,
    },
  }

  return new PublicClientApplication(config)
}

export async function initializeMsal(instance: PublicClientApplication): Promise<void> {
  await instance.initialize()
  const redirectResult = await instance.handleRedirectPromise()
  const account = redirectResult?.account ?? instance.getAllAccounts()[0] ?? null
  instance.setActiveAccount(account)
}