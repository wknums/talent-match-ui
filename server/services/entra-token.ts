import {
  createRemoteJWKSet,
  jwtVerify,
  type JWTVerifyGetKey,
  type JWTPayload,
} from 'jose'
import type { AuthErrorCode, UserRole } from '../../src/types/index.js'

const supportedRoles = new Set<UserRole>([
  'admin',
  'organization_admin',
  'recruiter',
  'business_panel',
])

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

export interface EntraTokenConfig {
  tenantId: string
  audience: string
  authorizedClientIds: string[]
  requiredScope: string
  issuer?: string
  metadataUrl?: string
  maxTokenAgeSeconds?: number
  clockToleranceSeconds?: number
}

export interface EntraTokenValidatorOptions {
  keyResolver?: JWTVerifyGetKey
  now?: () => Date
  fetch?: typeof fetch
}

export interface NormalizedEntraClaims {
  tenantId: string
  objectId: string
  authorizedClientId: string
  scopes: string[]
  roles: UserRole[]
  groups: string[]
  username: string
  fullName: string
  email?: string
  issuedAt: Date
  expiresAt: Date
}

export class EntraTokenError extends Error {
  readonly code: AuthErrorCode
  readonly statusCode: 401 | 403

  constructor(code: AuthErrorCode, message: string, statusCode: 401 | 403 = 401) {
    super(message)
    this.name = 'EntraTokenError'
    this.code = code
    this.statusCode = statusCode
  }
}

function requireUuid(value: unknown, claim: string): string {
  if (typeof value !== 'string' || !uuidPattern.test(value)) {
    throw new EntraTokenError('invalid_token', `The ${claim} claim is invalid.`)
  }
  return value.toLowerCase()
}

function hasGroupOverage(payload: JWTPayload): boolean {
  const claimNames = payload._claim_names
  return typeof claimNames === 'object'
    && claimNames !== null
    && 'groups' in claimNames
}

function normalizeStringArray(value: unknown, claim: string): string[] {
  if (value === undefined) return []
  if (!Array.isArray(value) || value.some(item => typeof item !== 'string')) {
    throw new EntraTokenError('invalid_token', `The ${claim} claim is invalid.`)
  }
  return value as string[]
}

function createCachedOidcKeyResolver(
  metadataUrl: string,
  expectedIssuer: string,
  fetchImpl: typeof fetch,
): JWTVerifyGetKey {
  let resolverPromise: Promise<JWTVerifyGetKey> | undefined

  const getResolver = () => {
    resolverPromise ??= fetchImpl(metadataUrl)
      .then(async response => {
        if (!response.ok) {
          throw new Error(`OIDC metadata request failed with status ${response.status}`)
        }
        return response.json() as Promise<{ issuer?: unknown; jwks_uri?: unknown }>
      })
      .then(metadata => {
        if (metadata.issuer !== expectedIssuer || typeof metadata.jwks_uri !== 'string') {
          throw new Error('OIDC metadata does not match the configured tenant issuer')
        }
        const jwksUrl = new URL(metadata.jwks_uri)
        if (jwksUrl.protocol !== 'https:') {
          throw new Error('OIDC JWKS endpoint must use HTTPS')
        }
        return createRemoteJWKSet(jwksUrl)
      })
      .catch(error => {
        resolverPromise = undefined
        throw error
      })

    return resolverPromise
  }

  return async (protectedHeader, token) => {
    const resolver = await getResolver()
    return resolver(protectedHeader, token)
  }
}

export function createEntraTokenValidator(
  config: EntraTokenConfig,
  options: EntraTokenValidatorOptions = {},
) {
  const tenantId = requireUuid(config.tenantId, 'configured tenant')
  const issuer = config.issuer ?? `https://login.microsoftonline.com/${tenantId}/v2.0`
  const metadataUrl = config.metadataUrl
    ?? `https://login.microsoftonline.com/${tenantId}/v2.0/.well-known/openid-configuration`
  const now = options.now ?? (() => new Date())
  const keyResolver = options.keyResolver
    ?? createCachedOidcKeyResolver(metadataUrl, issuer, options.fetch ?? fetch)
  const authorizedClients = new Set(config.authorizedClientIds.map(value => value.toLowerCase()))
  const maxTokenAgeSeconds = config.maxTokenAgeSeconds ?? 15 * 60
  const clockToleranceSeconds = config.clockToleranceSeconds ?? 30

  return {
    async validate(token: string): Promise<NormalizedEntraClaims> {
      if (!token) {
        throw new EntraTokenError('auth_required', 'A bearer access token is required.')
      }

      let payload: JWTPayload
      try {
        const verified = await jwtVerify(token, keyResolver, {
          algorithms: ['RS256'],
          currentDate: now(),
          clockTolerance: clockToleranceSeconds,
        })
        payload = verified.payload
      } catch (error) {
        if (error instanceof EntraTokenError) throw error
        throw new EntraTokenError('invalid_token', 'The access token is invalid or expired.')
      }

      if (payload.ver !== '2.0' || payload.iss !== issuer) {
        throw new EntraTokenError('invalid_token', 'The access token issuer is invalid.')
      }

      const tokenTenantId = requireUuid(payload.tid, 'tid')
      if (tokenTenantId !== tenantId) {
        throw new EntraTokenError('wrong_tenant', 'Sign in with an account from the configured tenant.')
      }

      if (payload.aud !== config.audience) {
        throw new EntraTokenError('invalid_audience', 'The access token is not intended for this API.')
      }

      const authorizedClientId = requireUuid(payload.azp, 'azp')
      if (!authorizedClients.has(authorizedClientId)) {
        throw new EntraTokenError('unauthorized_client', 'The calling application is not authorized.')
      }

      const scopes = typeof payload.scp === 'string'
        ? payload.scp.split(/\s+/).filter(Boolean)
        : []
      if (!scopes.includes(config.requiredScope)) {
        throw new EntraTokenError('invalid_token', 'The required delegated API scope is missing.')
      }

      if (typeof payload.iat !== 'number' || typeof payload.exp !== 'number') {
        throw new EntraTokenError('invalid_token', 'The access token lifetime claims are missing.')
      }
      const tokenAgeSeconds = Math.floor(now().getTime() / 1000) - payload.iat
      if (tokenAgeSeconds > maxTokenAgeSeconds + clockToleranceSeconds) {
        throw new EntraTokenError('token_stale', 'Your sign-in must be refreshed before continuing.')
      }

      const roles = normalizeStringArray(payload.roles, 'roles')
      if (roles.some(role => !supportedRoles.has(role as UserRole))) {
        throw new EntraTokenError('role_conflict', 'The token contains an unsupported application role.', 403)
      }

      if (hasGroupOverage(payload)) {
        throw new EntraTokenError('scope_unmapped', 'Group scope could not be resolved from this token.', 403)
      }
      const groups = normalizeStringArray(payload.groups, 'groups').map(group => requireUuid(group, 'groups'))

      const username = typeof payload.preferred_username === 'string'
        ? payload.preferred_username
        : objectIdFromPayload(payload)
      const fullName = typeof payload.name === 'string' ? payload.name : username
      const email = typeof payload.email === 'string' ? payload.email : undefined

      return {
        tenantId: tokenTenantId,
        objectId: objectIdFromPayload(payload),
        authorizedClientId,
        scopes,
        roles: roles as UserRole[],
        groups,
        username,
        fullName,
        email,
        issuedAt: new Date(payload.iat * 1000),
        expiresAt: new Date(payload.exp * 1000),
      }
    },
  }
}

function objectIdFromPayload(payload: JWTPayload): string {
  return requireUuid(payload.oid, 'oid')
}