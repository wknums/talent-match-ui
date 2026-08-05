import { createRequire } from 'node:module'
import type { DefaultAzureCredential } from '@azure/identity'

const require = createRequire(import.meta.url)

interface UserContext {
  username: string
  role: string
}

let _credential: DefaultAzureCredential | null = null

function getCredential(): DefaultAzureCredential {
  if (!_credential) {
    // Dynamic import is resolved at module level; we lazy-init the credential instance
    const { DefaultAzureCredential } = require('@azure/identity') as typeof import('@azure/identity')
    _credential = new DefaultAzureCredential()
  }
  return _credential
}

/**
 * Returns authentication headers for outbound requests to the AWReason API
 * based on the AWR_AUTH_MODE environment variable.
 *
 * Modes:
 *  - "none"   → no headers (local dev)
 *  - "apikey" → X-Api-Key + X-User-Id + X-User-Role (staging)
 *  - "entra"  → Authorization: Bearer <JWT> via DefaultAzureCredential (production)
 */
export async function getAwrAuthHeaders(
  user?: UserContext
): Promise<Record<string, string>> {
  const mode = (process.env.AWR_AUTH_MODE || 'none').toLowerCase()

  switch (mode) {
    case 'none':
      return {}

    case 'apikey': {
      const apiKey = process.env.AWR_API_KEY
      if (!apiKey) {
        throw new Error(
          'AWR_AUTH_MODE is "apikey" but AWR_API_KEY is not set. ' +
          'Set AWR_API_KEY in your environment variables.'
        )
      }
      const headers: Record<string, string> = {
        'X-Api-Key': apiKey,
      }
      if (user) {
        headers['X-User-Id'] = user.username
        headers['X-User-Role'] = user.role
      }
      return headers
    }

    case 'entra': {
      const audience = process.env.AWR_AAD_AUDIENCE
      if (!audience) {
        throw new Error(
          'AWR_AUTH_MODE is "entra" but AWR_AAD_AUDIENCE is not set. ' +
          'Set AWR_AAD_AUDIENCE to the target API scope/audience.'
        )
      }
      const credential = getCredential()
      const token = await credential.getToken(audience)
      return {
        Authorization: `Bearer ${token.token}`,
      }
    }

    default:
      throw new Error(
        `Invalid AWR_AUTH_MODE: "${mode}". Valid values: none, apikey, entra.`
      )
  }
}

/**
 * Validates that required AWR auth environment variables are set for the
 * configured mode. Call at startup to fail fast on misconfiguration.
 */
export function validateAwrAuthConfig(): void {
  const mode = (process.env.AWR_AUTH_MODE || 'none').toLowerCase()

  switch (mode) {
    case 'none':
      break
    case 'apikey':
      if (!process.env.AWR_API_KEY) {
        throw new Error(
          'AWR_AUTH_MODE is "apikey" but AWR_API_KEY is not set.'
        )
      }
      break
    case 'entra':
      if (!process.env.AWR_AAD_AUDIENCE) {
        throw new Error(
          'AWR_AUTH_MODE is "entra" but AWR_AAD_AUDIENCE is not set.'
        )
      }
      break
    default:
      throw new Error(
        `Invalid AWR_AUTH_MODE: "${mode}". Valid values: none, apikey, entra.`
      )
  }
}
